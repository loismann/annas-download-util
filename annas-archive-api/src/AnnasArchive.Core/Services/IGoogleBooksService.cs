using System.Collections.Generic;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for fetching book descriptions and cover images from Google Books API
/// </summary>
public interface IGoogleBooksService
{
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
