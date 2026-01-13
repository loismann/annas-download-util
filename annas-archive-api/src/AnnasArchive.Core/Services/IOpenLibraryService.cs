using System.Collections.Generic;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Author suggestion with confidence level.
/// </summary>
public record OpenLibraryAuthorSuggestion(string Author, string Confidence);

/// <summary>
/// Service for fetching book descriptions and metadata from OpenLibrary.org
/// </summary>
public interface IOpenLibraryService
{
    /// <summary>
    /// Fetches a book description from OpenLibrary with multiple fallback strategies:
    /// 1. Works API description field
    /// 2. Edition API description field
    /// 3. Search API first_sentence field
    /// (Books API excerpts are intentionally skipped as they often contain book text rather than summaries)
    /// Returns null if no description is found in any source.
    /// </summary>
    Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null);

    /// <summary>
    /// Fetches author suggestions for a book title from OpenLibrary.
    /// Results are cached for 6 hours.
    /// </summary>
    Task<List<OpenLibraryAuthorSuggestion>> GetAuthorSuggestionsAsync(string title);

    /// <summary>
    /// Fetches the best cover image URL for a book from OpenLibrary.
    /// </summary>
    Task<string?> GetCoverUrlAsync(string title, string? author = null);

    /// <summary>
    /// Fetches multiple cover image candidates for a book from OpenLibrary.
    /// </summary>
    Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12);
}
