using System.Text.Json;
using System.Text.Json.Serialization;

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
    string? Description,
    string[]? FavoritedBy = null,
    DateTime? CullReviewedAt = null)
{
    public string? Title { get; set; } = Title;
    public string[]? Authors { get; set; } = Authors;
    public string? CoverUrl { get; set; } = CoverUrl;
    public string? PrimaryGenre { get; set; } = PrimaryGenre;
    public string[]? Tags { get; set; } = Tags;
    public string? Series { get; set; } = Series;
    public double? GoodreadsRating { get; set; } = GoodreadsRating;
    public int? PersonalRating { get; set; } = PersonalRating;
    /// <summary>Names of household members ("Paul"/"Mom"/"Dad") who have favorited this book. Per-owner — replaces the old flat "Bookmarked" flag.</summary>
    public string[]? FavoritedBy { get; set; } = FavoritedBy;
    /// <summary>Set once Paul explicitly chooses "keep" in the daily library-review modal's cull phase. Null = not yet reviewed.</summary>
    public DateTime? CullReviewedAt { get; set; } = CullReviewedAt;

    /// <summary>Every edit endpoint rewrites the whole .meta.json through this model, so any
    /// field the model doesn't declare would be silently deleted on save. That's what kept
    /// stripping enrichmentComplete/aiEnrichedAt/openLibraryConfidence, re-arming the
    /// LibraryWatcher to re-enrich (and clobber) exactly the books users had personalized.
    /// This catch-all round-trips those markers — and anything added later — untouched.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public record LibraryBookMetadataUpdate(
    string PrimaryGenre,
    string[]? Tags,
    string? Series,
    string? Title,
    string[]? Authors);

public record LibraryBookRatingsUpdate(
    double? GoodreadsRating,
    int? PersonalRating);

public record LibraryBookFavoriteUpdate(bool Favorited);


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
    string[] FavoritedBy,
    DateTime? CullReviewedAt);
