namespace AnnasArchive.API.Services.Library;

/// <summary>
/// What OpenLibrary had to say about a book. <paramref name="Confidence"/> is
/// the whole reason this type exists: OpenLibrary answers almost every query
/// with <em>something</em>, so the useful question is never "did it match" but
/// "how sure are we", and the pipeline branches on that number three times.
/// </summary>
public sealed record OpenLibraryData(
    string? CoverUrl,
    string? PrimaryGenre,
    string[] Tags,
    string? Series,
    string? Title,
    string[] Authors,
    int? FirstPublishYear,
    double Confidence,
    string[] Isbns);

/// <summary>
/// The model's verdict on a low-confidence OpenLibrary match.
/// <paramref name="UseOpenLibrary"/> is not a formality — "the catalogue was
/// right, you just didn't recognise it" and "here is the real title" lead down
/// completely different branches, and only the second one earns a second
/// catalogue lookup.
/// </summary>
public sealed record AiValidationAndEnrichment(
    bool UseOpenLibrary,
    string? Title,
    string[] Authors,
    string? PublishedDate,
    string? Series,
    string? CoverUrl);

/// <summary>
/// The <c>.meta.json</c> already on disk. Read twice per enrichment pass — once
/// at the start for the values to build on, and once again immediately before
/// writing, because a full pass takes long enough for someone to have edited
/// the book in the browser meanwhile.
/// </summary>
public sealed class ExistingMeta
{
    public string? Title { get; init; }
    public string[]? Authors { get; init; }
    public string? CoverUrl { get; init; }
    public string? Source { get; init; }
    public string? Md5 { get; init; }
    public string? SavedAt { get; init; }
    public string? PrimaryGenre { get; init; }
    public string[]? Tags { get; init; }
    public string? Series { get; init; }
    public string[]? Genres { get; init; }
    public string? PublishedDate { get; init; }
    public string? Pages { get; init; }
    public double? GoodreadsRating { get; init; }
    public int? PersonalRating { get; init; }
    public string? Description { get; init; }
    public double? OpenLibraryConfidence { get; init; }
    public string? AiEnrichedAt { get; init; }
    public bool EnrichmentComplete { get; init; }
    public string[]? FavoritedBy { get; init; }
    public DateTime? CullReviewedAt { get; init; }

    public bool HasCoreMetadata =>
        !string.IsNullOrWhiteSpace(Title) &&
        Authors != null &&
        Authors.Length > 0;
}
