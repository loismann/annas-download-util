using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

public interface IYouTubeDownloadService
{
    Task<VideoInfo> GetVideoInfoAsync(string url, CancellationToken token);
    Task<string> StartDownloadAsync(StartDownloadRequest request, CancellationToken token);
    DownloadJob? GetJobStatus(string jobId);
    IEnumerable<DownloadJob> GetAllJobs();
    bool CancelJob(string jobId);
    bool DeleteJob(string jobId);
    IAsyncEnumerable<DownloadProgressEvent> StreamProgressAsync(string jobId, CancellationToken token);
}

public class YouTubeDownloadService : IYouTubeDownloadService, IDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJobState> _jobs = new();
    private readonly string _downloadRoot;
    private readonly string _ytDlpPath;
    private readonly string _nodePath;
    private readonly Timer _cleanupTimer;

    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+(?<percent>[\d.]+)%\s+of\s+~?\s*(?<size>[\d.]+\s*\w+)(\s+at\s+(?<speed>[\d.]+\s*\w+/s))?(\s+ETA\s+(?<eta>[\d:]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex DestinationRegex = new(
        @"\[download\]\s+Destination:\s+(?<path>.+)",
        RegexOptions.Compiled
    );

    private static readonly Regex MergerRegex = new(
        @"\[Merger\]\s+Merging formats into\s+""(?<path>.+)""",
        RegexOptions.Compiled
    );

    private static readonly Regex AlreadyDownloadedRegex = new(
        @"\[download\]\s+(?<path>.+)\s+has already been downloaded",
        RegexOptions.Compiled
    );

    private static readonly Regex ExtractorRegex = new(
        @"\[(?<extractor>youtube|youtube:tab|generic)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex VideoDownloadRegex = new(
        @"\[download\]\s+Downloading\s+video\s+(\d+\s+of\s+\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex AudioDownloadRegex = new(
        @"\[download\]\s+Downloading\s+audio\s+(\d+\s+of\s+\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex PostProcessorRegex = new(
        @"\[(Merger|FixupM3u8|ThumbnailsConvertor|ExtractAudio|EmbedThumbnail|ffmpeg|Fixup)\]",
        RegexOptions.Compiled
    );

    public YouTubeDownloadService(IConfiguration configuration)
    {
        _downloadRoot = Environment.GetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT")
            ?? configuration.GetValue<string>("YouTube:DownloadRoot")
            ?? "/volume1/media/YouTube";

        _ytDlpPath = configuration.GetValue<string>("YouTube:YtDlpPath")
            ?? (File.Exists("/usr/local/bin/yt-dlp") ? "/usr/local/bin/yt-dlp" : "yt-dlp");

        // Node.js path for yt-dlp JavaScript runtime (required since late 2024)
        _nodePath = Environment.GetEnvironmentVariable("NODE_PATH")
            ?? configuration.GetValue<string>("YouTube:NodePath")
            ?? (File.Exists("/usr/local/bin/node") ? "/usr/local/bin/node" : "node");

        Directory.CreateDirectory(_downloadRoot);

        // Cleanup old jobs every hour
        _cleanupTimer = new Timer(CleanupOldJobs, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        Log.Information("[YouTube] Service initialized. Download root: {Root}, Node: {NodePath}", _downloadRoot, _nodePath);
    }

    public async Task<VideoInfo> GetVideoInfoAsync(string url, CancellationToken token)
    {
        Log.Information("[YouTube] Fetching video info for: {Url}", url);

        // Use --js-runtimes with explicit node path for YouTube extraction (required since late 2024)
        var args = $"--js-runtimes node:{_nodePath} -j --no-download \"{url}\"";

        // Use StringBuilder to collect output - event-based pattern prevents buffer deadlocks
        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Use event handlers to read output asynchronously (prevents buffer deadlock)
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Use our own 2-minute timeout, NOT the client's cancellation token
        // This ensures the operation completes even if the client disconnects
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(cts.Token);

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                Log.Warning("[YouTube] Failed to get video info: {Error}", error);
                throw new InvalidOperationException($"Failed to get video info: {error}");
            }

            var info = ParseVideoInfo(output);
            Log.Information("[YouTube] Fetched info for: {Title}", info.Title);
            return info;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new InvalidOperationException("Timed out fetching video info. The video may be unavailable or yt-dlp is slow to respond.");
        }
    }

    private static VideoInfo ParseVideoInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.GetProperty("title").GetString() ?? "Unknown";
        var uploader = root.TryGetProperty("uploader", out var up) ? up.GetString() ?? "Unknown" : "Unknown";
        var durationSeconds = root.TryGetProperty("duration", out var dur) ? GetIntFromJson(dur) : 0;
        var duration = durationSeconds > 0 ? FormatDuration(durationSeconds) : "Unknown";
        var thumbnail = root.TryGetProperty("thumbnail", out var thumb) ? thumb.GetString() ?? "" : "";
        var videoId = root.TryGetProperty("id", out var vid) ? vid.GetString() : null;
        var description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null;

        // Try to get resolution from the format info
        string? bestResolution = null;
        if (root.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
        {
            bestResolution = $"{GetIntFromJson(h)}p";
        }

        var formats = new List<VideoFormat>();

        if (root.TryGetProperty("formats", out var formatsArray))
        {
            foreach (var format in formatsArray.EnumerateArray())
            {
                var formatId = format.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "" : "";
                var ext = format.TryGetProperty("ext", out var e) ? e.GetString() ?? "" : "";
                var resolution = GetResolution(format);
                var fileSize = GetFileSize(format);
                var quality = format.TryGetProperty("format_note", out var q) ? q.GetString() ?? "" : "";
                var vcodec = format.TryGetProperty("vcodec", out var vc) ? vc.GetString() ?? "none" : "none";
                var isAudioOnly = vcodec == "none" || string.IsNullOrEmpty(vcodec);

                // Skip formats without useful info
                if (string.IsNullOrEmpty(formatId)) continue;

                formats.Add(new VideoFormat(formatId, resolution, ext, fileSize, quality, isAudioOnly));
            }
        }

        // Add combined best formats
        formats.Insert(0, new VideoFormat("bestvideo+bestaudio/best", "Best Quality", "mp4/mkv", "~Auto", "best", false));
        formats.Insert(1, new VideoFormat("bestaudio/best", "Best Audio", "m4a/webm", "~Auto", "audio", true));

        return new VideoInfo(title, uploader, duration, durationSeconds, thumbnail, videoId, description, bestResolution, formats);
    }

    private static string GetResolution(JsonElement format)
    {
        if (format.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
        {
            var height = GetIntFromJson(h);
            var fps = format.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number
                ? GetIntFromJson(f)
                : 0;
            return fps > 30 ? $"{height}p{fps}" : $"{height}p";
        }

        if (format.TryGetProperty("resolution", out var res))
            return res.GetString() ?? "audio only";

        return "audio only";
    }

    // Helper to handle both int and float JSON values
    private static int GetIntFromJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)element.GetDouble(),
            _ => 0
        };
    }

    private static string GetFileSize(JsonElement format)
    {
        if (format.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number)
            return FormatBytes(GetLongFromJson(fs));

        if (format.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number)
            return $"~{FormatBytes(GetLongFromJson(fsa))}";

        return "Unknown";
    }

    private static long GetLongFromJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)element.GetDouble(),
            _ => 0
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.#}{sizes[order]}";
    }

    private static string FormatDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public async Task<string> StartDownloadAsync(StartDownloadRequest request, CancellationToken token)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];

        // Get video info for metadata
        var info = await GetVideoInfoAsync(request.Url, token);

        var state = new DownloadJobState
        {
            JobId = jobId,
            Url = request.Url,
            Title = request.OutputName ?? info.Title,
            Status = "queued",
            StatusMessage = "Starting download...",
            CancellationSource = new CancellationTokenSource(),
            // Store metadata for video library
            Channel = info.Uploader,
            Duration = info.Duration,
            DurationSeconds = info.DurationSeconds,
            ThumbnailUrl = info.Thumbnail,
            YouTubeId = info.VideoId,
            Description = info.Description,
            Resolution = info.Resolution
        };

        _jobs[jobId] = state;

        Log.Information("[YouTube] Starting download job {JobId} for {Title}", jobId, state.Title);

        // Fire and forget the download
        _ = ExecuteDownloadAsync(state, request.FormatId);

        return jobId;
    }

    private async Task ExecuteDownloadAsync(DownloadJobState state, string formatId)
    {
        var token = state.CancellationSource?.Token ?? CancellationToken.None;

        try
        {
            state.Status = "downloading";

            var safeTitle = SanitizeFileName(state.Title);
            var outputTemplate = Path.Combine(_downloadRoot, $"{safeTitle}.%(ext)s");

            // Include --write-thumbnail to save thumbnail for video library
            // Use --newline for parseable progress output
            // Use --js-runtimes with explicit node path for YouTube extraction (required since late 2024)
            // Use --extractor-args to force android client which has fewer restrictions
            // Note: Removed --convert-thumbnails as it causes "Option not found" on some yt-dlp versions
            var args = $"--js-runtimes node:{_nodePath} --extractor-args \"youtube:player_client=android\" -f \"{formatId}\" -o \"{outputTemplate}\" --write-thumbnail --newline \"{state.Url}\"";

            Log.Information("[YouTube] Running: {YtDlp} {Args}", _ytDlpPath, args);

            // Collect error lines to show meaningful error messages
            var errorLines = new List<string>();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (_, e) => ParseOutputLine(state, e.Data);
            // yt-dlp outputs progress to stderr, so parse it too
            process.ErrorDataReceived += (_, e) =>
            {
                ParseOutputLine(state, e.Data);
                // Only collect actual ERROR lines (not warnings) for user-facing error messages
                if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    errorLines.Add(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Log.Information("[YouTube] {JobId}: Download process started, waiting for completion...", state.JobId);

            var downloadStart = DateTime.UtcNow;
            await process.WaitForExitAsync(token);
            var downloadDuration = DateTime.UtcNow - downloadStart;

            Log.Information("[YouTube] {JobId}: Process exited with code {ExitCode} after {Duration:F1}s",
                state.JobId, process.ExitCode, downloadDuration.TotalSeconds);

            if (token.IsCancellationRequested)
            {
                state.Status = "cancelled";
                state.StatusMessage = "Download cancelled";
                Log.Information("[YouTube] Job {JobId} cancelled", state.JobId);
            }
            else if (process.ExitCode == 0)
            {
                state.Status = "complete";
                state.ProgressPercent = 100;
                state.StatusMessage = "Download complete";
                state.CompletedAt = DateTime.UtcNow;
                Log.Information("[YouTube] Job {JobId} completed: {Path}", state.JobId, state.OutputPath);

                // Create metadata file for video library
                state.StatusMessage = "Creating metadata...";
                await CreateVideoMetadataAsync(state);
                state.StatusMessage = "Download complete";
            }
            else
            {
                state.Status = "failed";
                // Include actual error message from yt-dlp if available
                var errorMessage = errorLines.Count > 0
                    ? string.Join("; ", errorLines.TakeLast(5))
                    : $"yt-dlp exited with code {process.ExitCode}";
                state.Error = errorMessage;
                state.StatusMessage = "Download failed";
                Log.Warning("[YouTube] Job {JobId} failed (exit code {ExitCode}): {Error}", state.JobId, process.ExitCode, errorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            state.Status = "cancelled";
            Log.Information("[YouTube] Job {JobId} cancelled", state.JobId);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            Log.Error(ex, "[YouTube] Job {JobId} failed with exception", state.JobId);
        }
    }

    private async Task CreateVideoMetadataAsync(DownloadJobState state)
    {
        if (string.IsNullOrWhiteSpace(state.OutputPath))
        {
            Log.Warning("[YouTube] Cannot create metadata - no output path for job {JobId}", state.JobId);
            return;
        }

        try
        {
            var fileName = Path.GetFileName(state.OutputPath);
            var fileInfo = new FileInfo(state.OutputPath);
            var fileSize = fileInfo.Exists ? VideoHelpers.FormatFileSize(fileInfo.Length) : "Unknown";
            var format = VideoHelpers.GetVideoFormat(fileName);

            // Find thumbnail - yt-dlp may save it with different naming patterns
            var baseName = Path.GetFileNameWithoutExtension(state.OutputPath);
            string? thumbnailUrl = null;

            // Check for various thumbnail patterns yt-dlp might use
            var possibleThumbnails = new[]
            {
                $"{baseName}.jpg",
                $"{baseName}.webp",
                $"{baseName}.png",
                $"{baseName}.jpeg"
            };

            foreach (var thumbName in possibleThumbnails)
            {
                var thumbPath = Path.Combine(_downloadRoot, thumbName);
                if (File.Exists(thumbPath))
                {
                    thumbnailUrl = thumbName;
                    Log.Information("[YouTube] Found thumbnail: {ThumbnailPath}", thumbPath);
                    break;
                }
            }

            // Also check for thumbnails with numeric suffixes (e.g., video.1.jpg)
            if (thumbnailUrl == null)
            {
                var thumbFiles = Directory.GetFiles(_downloadRoot, $"{baseName}*.jpg")
                    .Concat(Directory.GetFiles(_downloadRoot, $"{baseName}*.webp"))
                    .Concat(Directory.GetFiles(_downloadRoot, $"{baseName}*.png"))
                    .ToArray();

                if (thumbFiles.Length > 0)
                {
                    thumbnailUrl = Path.GetFileName(thumbFiles[0]);
                    Log.Information("[YouTube] Found thumbnail via pattern match: {ThumbnailPath}", thumbFiles[0]);
                }
            }

            if (thumbnailUrl == null)
            {
                Log.Warning("[YouTube] No thumbnail found for {BaseName}", baseName);
            }

            var meta = new VideoMeta(
                Title: state.Title,
                Channel: state.Channel,
                Duration: state.Duration,
                DurationSeconds: state.DurationSeconds,
                Format: format,
                Resolution: state.Resolution,
                FileSize: fileSize,
                FileName: fileName,
                ThumbnailUrl: thumbnailUrl,
                Description: state.Description,
                PrimaryGenre: null,
                Tags: null,
                Playlist: null,
                YouTubeId: state.YouTubeId,
                SourceUrl: state.Url,
                PersonalRating: null,
                Bookmarked: null,
                DownloadedAt: DateTime.UtcNow,
                PublishedAt: null
            );

            await VideoHelpers.WriteVideoMetadataAsync(_downloadRoot, meta);
            Log.Information("[YouTube] Created metadata file for {FileName}", fileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[YouTube] Failed to create metadata for job {JobId}", state.JobId);
        }
    }

    private static void ParseOutputLine(DownloadJobState state, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Log all output for debugging (at Debug level to avoid noise)
        Log.Debug("[YouTube] {JobId} output: {Line}", state.JobId, line);

        // Check for extractor info (extracting video metadata)
        if (ExtractorRegex.IsMatch(line))
        {
            state.StatusMessage = "Extracting video info...";
            Log.Information("[YouTube] {JobId}: Extracting video info", state.JobId);
            return;
        }

        // Check for video download start
        if (VideoDownloadRegex.IsMatch(line))
        {
            state.StatusMessage = "Downloading video stream...";
            Log.Information("[YouTube] {JobId}: Downloading video stream", state.JobId);
            return;
        }

        // Check for audio download start
        if (AudioDownloadRegex.IsMatch(line))
        {
            state.StatusMessage = "Downloading audio stream...";
            Log.Information("[YouTube] {JobId}: Downloading audio stream", state.JobId);
            return;
        }

        // Check for post-processing stages
        var postMatch = PostProcessorRegex.Match(line);
        if (postMatch.Success)
        {
            var processor = postMatch.Groups[1].Value;
            state.StatusMessage = processor switch
            {
                "Merger" => "Merging video and audio...",
                "ffmpeg" => "Processing with ffmpeg...",
                "ThumbnailsConvertor" => "Converting thumbnail...",
                "ExtractAudio" => "Extracting audio...",
                "EmbedThumbnail" => "Embedding thumbnail...",
                "FixupM3u8" or "Fixup" => "Fixing up file...",
                _ => $"Post-processing ({processor})..."
            };
            Log.Information("[YouTube] {JobId}: {Status}", state.JobId, state.StatusMessage);
            return;
        }

        // Check for progress updates - log any [download] line for debugging
        if (line.Contains("[download]"))
        {
            Log.Debug("[YouTube] {JobId}: Download line detected: {Line}", state.JobId, line);
        }

        var progressMatch = ProgressRegex.Match(line);
        if (progressMatch.Success)
        {
            var oldPercent = state.ProgressPercent;
            if (double.TryParse(progressMatch.Groups["percent"].Value, out var percent))
            {
                state.ProgressPercent = percent;
            }

            // Log every 10% milestone (but skip 0% to reduce noise from video/audio stream switches)
            var oldMilestone = (int)(oldPercent / 10);
            var newMilestone = (int)(state.ProgressPercent / 10);
            if (newMilestone > oldMilestone && newMilestone > 0)
            {
                Log.Information("[YouTube] {JobId}: {Percent:F0}% downloaded ({Speed}, ETA: {Eta})",
                    state.JobId, state.ProgressPercent, state.CurrentSpeed ?? "?", state.Eta ?? "?");
            }

            var speed = progressMatch.Groups["speed"].Value;
            if (!string.IsNullOrWhiteSpace(speed))
                state.CurrentSpeed = speed;

            var eta = progressMatch.Groups["eta"].Value;
            if (!string.IsNullOrWhiteSpace(eta))
                state.Eta = eta;

            return;
        }

        // Check for destination path
        var destMatch = DestinationRegex.Match(line);
        if (destMatch.Success)
        {
            state.OutputPath = destMatch.Groups["path"].Value;
            state.StatusMessage = "Downloading...";
            return;
        }

        // Check for merger output (final merged file)
        var mergerMatch = MergerRegex.Match(line);
        if (mergerMatch.Success)
        {
            state.OutputPath = mergerMatch.Groups["path"].Value;
            state.StatusMessage = "Merging video and audio...";
            return;
        }

        // Check for already downloaded
        var alreadyMatch = AlreadyDownloadedRegex.Match(line);
        if (alreadyMatch.Success)
        {
            state.OutputPath = alreadyMatch.Groups["path"].Value;
            state.ProgressPercent = 100;
            state.StatusMessage = "Already downloaded";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    public DownloadJob? GetJobStatus(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var state) ? state.ToRecord() : null;
    }

    public IEnumerable<DownloadJob> GetAllJobs()
    {
        return _jobs.Values
            .OrderByDescending(j => j.StartedAt)
            .Take(50)
            .Select(j => j.ToRecord());
    }

    public bool CancelJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
            return false;

        if (state.Status is not ("queued" or "downloading"))
            return false;

        state.CancellationSource?.Cancel();
        return true;
    }

    public bool DeleteJob(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out var state))
            return false;

        // Optionally delete the file too
        if (!string.IsNullOrWhiteSpace(state.OutputPath) && File.Exists(state.OutputPath))
        {
            try
            {
                File.Delete(state.OutputPath);
                Log.Information("[YouTube] Deleted file for job {JobId}: {Path}", jobId, state.OutputPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[YouTube] Failed to delete file for job {JobId}", jobId);
            }
        }

        return true;
    }

    public async IAsyncEnumerable<DownloadProgressEvent> StreamProgressAsync(
        string jobId,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
            yield break;

        var lastPercent = -1.0;
        var lastStatus = "";

        while (!token.IsCancellationRequested && state.Status is "queued" or "downloading")
        {
            // Only yield if there's a meaningful change
            if (Math.Abs(state.ProgressPercent - lastPercent) > 0.5 || state.Status != lastStatus)
            {
                lastPercent = state.ProgressPercent;
                lastStatus = state.Status;

                yield return new DownloadProgressEvent(
                    jobId,
                    state.Status,
                    state.ProgressPercent,
                    state.CurrentSpeed,
                    state.Eta,
                    null
                );
            }

            await Task.Delay(500, token);
        }

        // Final status
        yield return new DownloadProgressEvent(
            jobId,
            state.Status,
            state.ProgressPercent,
            null,
            null,
            state.Status switch
            {
                "complete" => "Download complete",
                "failed" => state.Error,
                "cancelled" => "Download cancelled",
                _ => null
            }
        );
    }

    private void CleanupOldJobs(object? state)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);

        foreach (var job in _jobs.Values.ToArray())
        {
            if (job.Status is "complete" or "failed" or "cancelled" && job.StartedAt < cutoff)
            {
                if (_jobs.TryRemove(job.JobId, out _))
                    Log.Debug("[YouTube] Cleaned up old job {JobId}", job.JobId);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();

        // Cancel all active downloads
        foreach (var job in _jobs.Values)
        {
            job.CancellationSource?.Cancel();
            job.CancellationSource?.Dispose();
        }
    }
}
