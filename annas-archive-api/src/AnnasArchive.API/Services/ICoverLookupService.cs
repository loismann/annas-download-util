namespace AnnasArchive.API.Services;

/// <summary>
/// Result from a cover lookup operation.
/// </summary>
/// <param name="CoverUrl">The cover URL, or null if not found</param>
/// <param name="Source">The source of the cover (e.g., "Google Books", "Open Library")</param>
public record CoverLookupResult(string? CoverUrl, string? Source);

/// <summary>
/// Service for fetching book covers from multiple sources with cascading fallback.
/// Consolidates the common pattern of trying multiple services for cover images.
/// </summary>
public interface ICoverLookupService
{
    /// <summary>
    /// Fetches a book cover using a cascade of sources:
    /// 1. Open Library API (usually higher quality)
    /// 2. Google Books API (fallback)
    /// </summary>
    /// <param name="title">The book title</param>
    /// <param name="author">The book author (optional)</param>
    /// <returns>A result containing the cover URL and its source</returns>
    Task<CoverLookupResult> GetCoverAsync(string title, string? author = null);

    /// <summary>
    /// Gets multiple cover candidates from all sources.
    /// </summary>
    /// <param name="title">The book title</param>
    /// <param name="author">The book author (optional)</param>
    /// <param name="limit">Maximum number of candidates to return</param>
    /// <returns>List of cover URLs from all sources</returns>
    Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12);

    /// <summary>
    /// Builds a list of title variations to try when searching for covers.
    /// Handles common patterns like subtitles, series info, and parentheticals.
    /// </summary>
    /// <param name="title">The original title</param>
    /// <returns>List of title candidates to search</returns>
    List<string> BuildTitleCandidates(string title);
}
