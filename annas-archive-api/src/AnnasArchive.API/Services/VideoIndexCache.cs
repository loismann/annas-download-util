using System.Collections.Concurrent;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Caches the video library index in memory to avoid reading files on each request.
/// Caching/watcher scaffolding lives in <see cref="MetaIndexCache{TDto}"/>; this
/// class owns the video-specific index build and thumbnail URL normalization.
/// </summary>
public class VideoIndexCache : MetaIndexCache<VideoDto>
{
    public VideoIndexCache()
        : base("VideoIndexCache", VideoHelpers.ResolveVideoRoot())
    {
    }

    /// <summary>
    /// Gets the cached videos, rebuilding the cache if necessary.
    /// </summary>
    public List<VideoDto> GetVideos(string baseUrl) => GetItems(baseUrl);

    /// <summary>
    /// Updates a single video in the cache without full rebuild.
    /// </summary>
    public void UpdateVideo(VideoDto updatedVideo) => UpdateItem(updatedVideo);

    /// <summary>
    /// Removes a video from the cache without full rebuild.
    /// </summary>
    public void RemoveVideo(string fileName) => RemoveItem(fileName);

    protected override string KeyOf(VideoDto item) => item.FileName;

    protected override List<VideoDto> SortIndex(IEnumerable<VideoDto> items) =>
        items.OrderByDescending(v => v.DownloadedAt ?? DateTime.MinValue).ToList();

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

    protected override List<VideoDto> NormalizeUrls(List<VideoDto> items, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return items;

        var videoRoot = VideoHelpers.ResolveVideoRoot();

        return items.Select(video =>
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

    protected override List<VideoDto> BuildIndex(string? baseUrl)
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

        return SortIndex(videos);
    }
}
