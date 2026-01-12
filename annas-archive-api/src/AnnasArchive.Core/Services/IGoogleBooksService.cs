using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for fetching book descriptions from Google Books API
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
}
