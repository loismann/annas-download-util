using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

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
    /// <param name="title">Book title</param>
    /// <param name="author">Book author</param>
    /// <param name="isbn">Optional ISBN for more accurate lookups</param>
    /// <returns>Book description or null if not found</returns>
    Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null);
}
