using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public YouTubeDownloadService(IConfiguration configuration)
    {
        _downloadRoot = Environment.GetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT")
            ?? configuration.GetValue<string>("YouTube:DownloadRoot")
            ?? "/volume1/media/YouTube";

        _ytDlpPath = configuration.GetValue<string>("YouTube:YtDlpPath")
            ?? (File.Exists("/usr/local/bin/yt-dlp") ? "/usr/local/bin/yt-dlp" : "yt-dlp");

        Directory.CreateDirectory(_downloadRoot);

        // Cleanup old jobs every hour
        _cleanupTimer = new Timer(CleanupOldJobs, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        Log.Information("[YouTube] Service initialized. Download root: {Root}", _downloadRoot);
    }

    public async Task<VideoInfo> GetVideoInfoAsync(string url, CancellationToken token)
    {
        var args = $"-j --no-download \"{url}\"";

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

        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0)
        {
            Log.Warning("[YouTube] Failed to get video info: {Error}", error);
            throw new InvalidOperationException($"Failed to get video info: {error}");
        }

        return ParseVideoInfo(output);
    }

    private static VideoInfo ParseVideoInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.GetProperty("title").GetString() ?? "Unknown";
        var uploader = root.TryGetProperty("uploader", out var up) ? up.GetString() ?? "Unknown" : "Unknown";
        var duration = root.TryGetProperty("duration", out var dur) ? FormatDuration(dur.GetInt32()) : "Unknown";
        var thumbnail = root.TryGetProperty("thumbnail", out var thumb) ? thumb.GetString() ?? "" : "";

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

        return new VideoInfo(title, uploader, duration, thumbnail, formats);
    }

    private static string GetResolution(JsonElement format)
    {
        if (format.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
        {
            var height = h.GetInt32();
            var fps = format.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number
                ? f.GetInt32()
                : 0;
            return fps > 30 ? $"{height}p{fps}" : $"{height}p";
        }

        if (format.TryGetProperty("resolution", out var res))
            return res.GetString() ?? "audio only";

        return "audio only";
    }

    private static string GetFileSize(JsonElement format)
    {
        if (format.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number)
            return FormatBytes(fs.GetInt64());

        if (format.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number)
            return $"~{FormatBytes(fsa.GetInt64())}";

        return "Unknown";
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

        // Get video title first
        var info = await GetVideoInfoAsync(request.Url, token);

        var state = new DownloadJobState
        {
            JobId = jobId,
            Url = request.Url,
            Title = request.OutputName ?? info.Title,
            Status = "queued",
            CancellationSource = new CancellationTokenSource()
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

            var args = $"-f \"{formatId}\" -o \"{outputTemplate}\" --newline --progress \"{state.Url}\"";

            Log.Debug("[YouTube] Running: {YtDlp} {Args}", _ytDlpPath, args);

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
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Log.Debug("[YouTube] stderr: {Line}", e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token);

            if (token.IsCancellationRequested)
            {
                state.Status = "cancelled";
                Log.Information("[YouTube] Job {JobId} cancelled", state.JobId);
            }
            else if (process.ExitCode == 0)
            {
                state.Status = "complete";
                state.ProgressPercent = 100;
                state.CompletedAt = DateTime.UtcNow;
                Log.Information("[YouTube] Job {JobId} completed: {Path}", state.JobId, state.OutputPath);
            }
            else
            {
                state.Status = "failed";
                state.Error = $"yt-dlp exited with code {process.ExitCode}";
                Log.Warning("[YouTube] Job {JobId} failed: {Error}", state.JobId, state.Error);
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

    private static void ParseOutputLine(DownloadJobState state, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Log.Debug("[YouTube] stdout: {Line}", line);

        // Check for progress updates
        var progressMatch = ProgressRegex.Match(line);
        if (progressMatch.Success)
        {
            if (double.TryParse(progressMatch.Groups["percent"].Value, out var percent))
                state.ProgressPercent = percent;

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
            return;
        }

        // Check for merger output (final merged file)
        var mergerMatch = MergerRegex.Match(line);
        if (mergerMatch.Success)
        {
            state.OutputPath = mergerMatch.Groups["path"].Value;
            return;
        }

        // Check for already downloaded
        var alreadyMatch = AlreadyDownloadedRegex.Match(line);
        if (alreadyMatch.Success)
        {
            state.OutputPath = alreadyMatch.Groups["path"].Value;
            state.ProgressPercent = 100;
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
