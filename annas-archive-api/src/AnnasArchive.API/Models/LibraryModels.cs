namespace AnnasArchive.API.Models;

/// <summary>
/// Models for library book management.
/// </summary>

public record LibraryBookMeta(
    string? Title,
    string[]? Authors,
    string? Format,
    string? FileSize,
    string FileName,
    string? CoverUrl,
    string? Source,
    string? Md5,
    DateTime? SavedAt,
    string? PrimaryGenre,
    string[]? Tags,
    string? Series,
    string[]? Genres,
    string? PublishedDate,
    string? Pages,
    double? GoodreadsRating,
    int? PersonalRating,
    bool? ReaderEnabled,
    string? Description,
    bool? Bookmarked = null)
{
    public string? Title { get; set; } = Title;
    public string[]? Authors { get; set; } = Authors;
    public string? CoverUrl { get; set; } = CoverUrl;
    public string? PrimaryGenre { get; set; } = PrimaryGenre;
    public string[]? Tags { get; set; } = Tags;
    public string? Series { get; set; } = Series;
    public double? GoodreadsRating { get; set; } = GoodreadsRating;
    public int? PersonalRating { get; set; } = PersonalRating;
    public bool? ReaderEnabled { get; set; } = ReaderEnabled;
    public bool? Bookmarked { get; set; } = Bookmarked;
}

public record LibraryBookMetadataUpdate(
    string PrimaryGenre,
    string[]? Tags,
    string? Series,
    string? Title,
    string[]? Authors);

public record LibraryBookRatingsUpdate(
    double? GoodreadsRating,
    int? PersonalRating,
    bool? Bookmarked);

public record LibraryBookReaderUpdate(bool? Enabled);

public record LibraryBookCoverUpdate(string CoverUrl);

public record LibraryBookCoverBytesUpdate(string ImageBase64, string? MimeType);

public record ReaderBookDto(
    string FileName,
    string ReaderKey,
    string Title,
    string[] Authors,
    string Format,
    string? CoverUrl,
    bool HasSummaries);

public record LibraryReaderIndexRequest(string FileName);

public record LibraryBookDto(
    string Title,
    string[] Authors,
    string Format,
    string FileSize,
    string FileName,
    string? CoverUrl,
    string? Source,
    string? Md5,
    DateTime? SavedAt,
    string? PrimaryGenre,
    string[] Tags,
    string? Series,
    string[] Genres,
    string? PublishedDate,
    string? Pages,
    double? GoodreadsRating,
    int? PersonalRating,
    bool? ReaderEnabled,
    bool? Bookmarked);
