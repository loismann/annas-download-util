namespace AnnasArchive.API.Models;

/// <summary>
/// Request to fetch available formats for a YouTube video.
/// </summary>
public record GetFormatsRequest(string Url);

/// <summary>
/// Represents a single video format option.
/// </summary>
public record VideoFormat(
    string FormatId,
    string Resolution,
    string Extension,
    string FileSize,
    string Quality,
    bool IsAudioOnly
);

/// <summary>
/// Video information returned from yt-dlp.
/// </summary>
public record VideoInfo(
    string Title,
    string Uploader,
    string Duration,
    long DurationSeconds,
    string Thumbnail,
    string? VideoId,
    string? Description,
    string? Resolution,
    List<VideoFormat> Formats
);

/// <summary>
/// Request to start a video download.
/// </summary>
public record StartDownloadRequest(
    string Url,
    string FormatId,
    string? OutputName = null
);

/// <summary>
/// Represents the current state of a download job.
/// </summary>
public record DownloadJob(
    string JobId,
    string Url,
    string Title,
    string Status,
    double ProgressPercent,
    string? CurrentSpeed,
    string? Eta,
    string? OutputPath,
    string? Error,
    string? StatusMessage,
    DateTime StartedAt,
    DateTime? CompletedAt
);

/// <summary>
/// Progress event sent via SSE during download.
/// </summary>
public record DownloadProgressEvent(
    string JobId,
    string Status,
    double ProgressPercent,
    string? CurrentSpeed,
    string? Eta,
    string? Message
);

/// <summary>
/// Internal mutable state for tracking download jobs.
/// </summary>
public class DownloadJobState
{
    public required string JobId { get; init; }
    public required string Url { get; init; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public double ProgressPercent { get; set; }
    public string? CurrentSpeed { get; set; }
    public string? Eta { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public CancellationTokenSource? CancellationSource { get; set; }

    // Additional metadata for video library
    public string? Channel { get; set; }
    public string? Duration { get; set; }
    public long? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? YouTubeId { get; set; }
    public string? Description { get; set; }
    public string? Resolution { get; set; }

    public DownloadJob ToRecord() => new(
        JobId,
        Url,
        Title,
        Status,
        ProgressPercent,
        CurrentSpeed,
        Eta,
        OutputPath,
        Error,
        StatusMessage,
        StartedAt,
        CompletedAt
    );
}
