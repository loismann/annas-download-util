using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// One Google Books volume, reduced to the fields anything in this codebase reads.
/// Deliberately not the raw <c>JsonElement</c>: callers that only want to score or
/// display a match should not have to know the API's response shape.
/// </summary>
/// <param name="Year">Parsed from <c>publishedDate</c>, which Google returns as
/// "1965", "1965-06" or "1965-06-01" depending on the record.</param>
public sealed record GoogleBooksVolume(
    string? Title,
    string[] Authors,
    int? Year,
    string? ThumbnailUrl);

/// <summary>
/// Service for fetching book descriptions and cover images from Google Books API
/// </summary>
public interface IGoogleBooksService
{
    /// <summary>
    /// Raw volume search — for callers that need to judge the candidates themselves
    /// rather than take this service's pick. Added so
    /// <c>AudiobookEnrichmentService</c> could stop hand-rolling the same HTTP call:
    /// it scores results with its own <c>TitleMatchScorer</c>, which no opinionated
    /// method here can express.
    /// </summary>
    /// <returns>
    /// The matches, or <c>null</c> if the request itself failed. The distinction
    /// matters: callers with rate-limit tracking need to tell "the API said no
    /// results" from "the API did not answer", and an empty list would conflate them.
    /// </returns>
    Task<IReadOnlyList<GoogleBooksVolume>?> SearchVolumesAsync(
        string query, int maxResults = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a book description from Google Books API.
    /// Returns null if no description is found.
    /// </summary>
    /// <param name="title">Book title</param>
    /// <param name="author">Book author</param>
    /// <param name="isbn">Optional ISBN for more accurate lookups</param>
    /// <returns>Book description or null if not found</returns>
    Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null);

    /// <summary>
    /// Fetches the best cover image URL for a book from Google Books.
    /// </summary>
    Task<string?> GetCoverUrlAsync(string title, string? author = null);

    /// <summary>
    /// Fetches multiple cover image candidates for a book from Google Books.
    /// </summary>
    Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12);
}
