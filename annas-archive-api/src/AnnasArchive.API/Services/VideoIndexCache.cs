using System.Collections.Concurrent;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Caches the video library index in memory to avoid reading files on each request.
/// Implements IHostedService to warm the cache on application startup.
/// Uses FileSystemWatcher to invalidate cache when files change.
/// </summary>
public class VideoIndexCache : IHostedService, IDisposable
{
    private readonly object _lock = new();
    private List<VideoDto>? _cachedVideos;
    private DateTime _lastBuildTime = DateTime.MinValue;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _pendingChanges = new();
    private Timer? _debounceTimer;
    private bool _isRebuilding;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);

    public VideoIndexCache()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// Warm the cache on application startup.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Build cache in background so startup isn't blocked
        _ = Task.Run(() =>
        {
            try
            {
                Log.Information("[VideoIndexCache] Warming cache on startup...");
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var videos = BuildVideoIndex(baseUrl: null);

                lock (_lock)
                {
                    _cachedVideos = videos;
                    _lastBuildTime = DateTime.UtcNow;
                }

                sw.Stop();
                Log.Information("[VideoIndexCache] Cache warmed on startup in {ElapsedMs}ms with {Count} videos",
                    sw.ElapsedMilliseconds, videos.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[VideoIndexCache] Failed to warm cache on startup");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void InitializeWatcher()
    {
        try
        {
            var videoRoot = VideoHelpers.ResolveVideoRoot();
            if (!Directory.Exists(videoRoot))
            {
                Log.Warning("[VideoIndexCache] Video root does not exist: {VideoRoot}", videoRoot);
                return;
            }

            _watcher = new FileSystemWatcher(videoRoot)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                Filter = "*.meta.json",
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;

            Log.Information("[VideoIndexCache] FileSystemWatcher initialized for {VideoRoot}", videoRoot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VideoIndexCache] Failed to initialize FileSystemWatcher");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _pendingChanges.Enqueue(e.FullPath);
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                InvalidateCache();
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedVideos = null;
            while (_pendingChanges.TryDequeue(out _)) { }
            Log.Information("[VideoIndexCache] Cache invalidated");
        }
    }

    /// <summary>
    /// Gets the cached videos, rebuilding the cache if necessary.
    /// </summary>
    public List<VideoDto> GetVideos(string baseUrl)
    {
        lock (_lock)
        {
            if (_cachedVideos != null)
            {
                return NormalizeThumbnailUrls(_cachedVideos, baseUrl);
            }
        }

        return RebuildCache(baseUrl);
    }

    /// <summary>
    /// Gets a paginated list of videos.
    /// </summary>
    public (List<VideoDto> Videos, int TotalCount) GetVideosPaginated(
        string baseUrl,
        int skip = 0,
        int take = 50,
        string sortBy = "date",
        bool sortDesc = true)
    {
        var allVideos = GetVideos(baseUrl);
        var totalCount = allVideos.Count;

        // Apply sorting
        IEnumerable<VideoDto> sorted = sortBy.ToLowerInvariant() switch
        {
            "title" => sortDesc
                ? allVideos.OrderByDescending(v => v.Title, StringComparer.OrdinalIgnoreCase)
                : allVideos.OrderBy(v => v.Title, StringComparer.OrdinalIgnoreCase),
            "channel" => sortDesc
                ? allVideos.OrderByDescending(v => v.Channel, StringComparer.OrdinalIgnoreCase)
                : allVideos.OrderBy(v => v.Channel, StringComparer.OrdinalIgnoreCase),
            "duration" => sortDesc
                ? allVideos.OrderByDescending(v => v.DurationSeconds ?? 0)
                : allVideos.OrderBy(v => v.DurationSeconds ?? 0),
            "rating" => sortDesc
                ? allVideos.OrderByDescending(v => v.PersonalRating ?? 0)
                : allVideos.OrderBy(v => v.PersonalRating ?? 0),
            "date" or _ => sortDesc
                ? allVideos.OrderByDescending(v => v.DownloadedAt ?? DateTime.MinValue)
                : allVideos.OrderBy(v => v.DownloadedAt ?? DateTime.MinValue)
        };

        // Apply pagination
        var paginated = sorted.Skip(skip);
        if (take > 0)
        {
            paginated = paginated.Take(take);
        }

        return (paginated.ToList(), totalCount);
    }

    private static List<VideoDto> NormalizeThumbnailUrls(List<VideoDto> videos, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return videos;

        var videoRoot = VideoHelpers.ResolveVideoRoot();

        return videos.Select(video =>
        {
            if (video.ThumbnailUrl?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
                return video;

            if (video.ThumbnailUrl?.StartsWith("/api/video-library/thumbnail/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var fullUrl = $"{baseUrl}{video.ThumbnailUrl}";
                return video with { ThumbnailUrl = fullUrl };
            }

            var normalizedUrl = VideoHelpers.NormalizeThumbnailUrl(video.ThumbnailUrl, baseUrl)
                ?? VideoHelpers.FindLocalThumbnailUrl(videoRoot, video.FileName, baseUrl);

            if (normalizedUrl == video.ThumbnailUrl)
                return video;

            return video with { ThumbnailUrl = normalizedUrl };
        }).ToList();
    }

    private List<VideoDto> RebuildCache(string baseUrl)
    {
        lock (_lock)
        {
            if (_cachedVideos != null)
            {
                return _cachedVideos;
            }

            if (_isRebuilding)
            {
                return new List<VideoDto>();
            }

            _isRebuilding = true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Information("[VideoIndexCache] Starting cache rebuild...");

        try
        {
            var videos = BuildVideoIndex(baseUrl);

            lock (_lock)
            {
                _cachedVideos = videos;
                _lastBuildTime = DateTime.UtcNow;
                _isRebuilding = false;
            }

            sw.Stop();
            Log.Information("[VideoIndexCache] Cache rebuilt in {ElapsedMs}ms with {Count} videos",
                sw.ElapsedMilliseconds, videos.Count);

            return videos;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VideoIndexCache] Failed to rebuild cache");
            lock (_lock)
            {
                _isRebuilding = false;
            }
            return new List<VideoDto>();
        }
    }

    private static List<VideoDto> BuildVideoIndex(string? baseUrl)
    {
        var videoRoot = VideoHelpers.ResolveVideoRoot();
        if (!Directory.Exists(videoRoot))
            return new List<VideoDto>();

        var metaFiles = Directory.GetFiles(videoRoot, "*.meta.json");
        var jsonOptions = VideoHelpers.CreateVideoJsonOptions();
        var videos = new ConcurrentBag<VideoDto>();
        var metaLookup = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Process meta files in parallel
        Parallel.ForEach(metaFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            metaFile =>
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var meta = JsonSerializer.Deserialize<VideoMeta>(json, jsonOptions);
                    if (meta == null)
                        return;

                    metaLookup.TryAdd(meta.FileName, true);
                    var thumbnailUrl = VideoHelpers.NormalizeThumbnailUrl(meta.ThumbnailUrl, baseUrl ?? "")
                        ?? VideoHelpers.FindLocalThumbnailUrl(videoRoot, meta.FileName, baseUrl ?? "");

                    videos.Add(new VideoDto(
                        meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                        meta.Channel ?? "Unknown",
                        meta.Duration ?? "Unknown",
                        meta.DurationSeconds,
                        meta.Format ?? VideoHelpers.GetVideoFormat(meta.FileName),
                        meta.Resolution,
                        meta.FileSize ?? "",
                        meta.FileName,
                        thumbnailUrl,
                        meta.Description,
                        meta.PrimaryGenre,
                        meta.Tags ?? Array.Empty<string>(),
                        meta.Playlist,
                        meta.YouTubeId,
                        meta.PersonalRating,
                        meta.Bookmarked,
                        meta.DownloadedAt,
                        meta.PublishedAt
                    ));
                }
                catch
                {
                    // Ignore malformed meta files
                }
            });

        // Process orphan video files (no meta)
        foreach (var filePath in Directory.GetFiles(videoRoot))
        {
            try
            {
                if (!VideoHelpers.IsSupportedVideoFile(filePath))
                    continue;

                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(fileName) || metaLookup.ContainsKey(fileName))
                    continue;

                var info = new FileInfo(filePath);
                var thumbnailUrl = VideoHelpers.FindLocalThumbnailUrl(videoRoot, fileName, baseUrl ?? "");

                videos.Add(new VideoDto(
                    Path.GetFileNameWithoutExtension(fileName),
                    "Unknown",
                    "Unknown",
                    null,
                    VideoHelpers.GetVideoFormat(fileName),
                    null,
                    VideoHelpers.FormatFileSize(info.Length),
                    fileName,
                    thumbnailUrl,
                    null,
                    null,
                    Array.Empty<string>(),
                    null,
                    null,
                    null,
                    null,
                    info.LastWriteTimeUtc,
                    null
                ));
            }
            catch (Exception ex)
            {
                Log.Debug("[VideoIndexCache] Skipping file {FilePath}: {Message}", filePath, ex.Message);
            }
        }

        return videos
            .OrderByDescending(v => v.DownloadedAt ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>
    /// Updates a single video in the cache without full rebuild.
    /// </summary>
    public void UpdateVideo(VideoDto updatedVideo)
    {
        lock (_lock)
        {
            if (_cachedVideos == null)
                return;

            var index = _cachedVideos.FindIndex(v =>
                string.Equals(v.FileName, updatedVideo.FileName, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _cachedVideos[index] = updatedVideo;
            }
            else
            {
                _cachedVideos.Add(updatedVideo);
                _cachedVideos = _cachedVideos
                    .OrderByDescending(v => v.DownloadedAt ?? DateTime.MinValue)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Removes a video from the cache without full rebuild.
    /// </summary>
    public void RemoveVideo(string fileName)
    {
        lock (_lock)
        {
            if (_cachedVideos == null)
                return;

            _cachedVideos.RemoveAll(v =>
                string.Equals(v.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DateTime LastBuildTime => _lastBuildTime;
    public int CachedVideoCount => _cachedVideos?.Count ?? 0;
    public bool IsCached => _cachedVideos != null;

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
