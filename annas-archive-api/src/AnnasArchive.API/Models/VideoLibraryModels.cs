namespace AnnasArchive.API.Models;

/// <summary>
/// Metadata stored in *.meta.json files alongside video files.
/// </summary>
public record VideoMeta(
    string? Title,
    string? Channel,
    string? Duration,
    long? DurationSeconds,
    string? Format,
    string? Resolution,
    string? FileSize,
    string FileName,
    string? ThumbnailUrl,
    string? Description,
    string? PrimaryGenre,
    string[]? Tags,
    string? Playlist,
    string? YouTubeId,
    string? SourceUrl,
    int? PersonalRating,
    bool? Bookmarked,
    DateTime? DownloadedAt,
    DateTime? PublishedAt)
{
    public string? Title { get; set; } = Title;
    public string? Channel { get; set; } = Channel;
    public string? PrimaryGenre { get; set; } = PrimaryGenre;
    public string[]? Tags { get; set; } = Tags;
    public string? Playlist { get; set; } = Playlist;
    public int? PersonalRating { get; set; } = PersonalRating;
    public bool? Bookmarked { get; set; } = Bookmarked;
}

/// <summary>
/// Request to update video metadata.
/// </summary>
public record VideoMetadataUpdate(
    string? PrimaryGenre,
    string[]? Tags,
    string? Playlist,
    string? Title,
    string? Channel);

/// <summary>
/// Request to update video ratings.
/// </summary>
public record VideoRatingsUpdate(
    int? PersonalRating,
    bool? Bookmarked);

/// <summary>
/// DTO returned to the frontend for video library display.
/// </summary>
public record VideoDto(
    string Title,
    string Channel,
    string Duration,
    long? DurationSeconds,
    string Format,
    string? Resolution,
    string FileSize,
    string FileName,
    string? ThumbnailUrl,
    string? Description,
    string? PrimaryGenre,
    string[] Tags,
    string? Playlist,
    string? YouTubeId,
    int? PersonalRating,
    bool? Bookmarked,
    DateTime? DownloadedAt,
    DateTime? PublishedAt);
