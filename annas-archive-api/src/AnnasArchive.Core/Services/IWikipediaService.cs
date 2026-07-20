using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for fetching book descriptions from Wikipedia. Used as a real-data
/// fallback alongside/instead of OpenLibrary and Google Books — free, no API
/// key, and not subject to the same rate limits that made those two
/// unreliable in practice.
/// </summary>
public interface IWikipediaService
{
    /// <summary>
    /// Searches Wikipedia for a page matching the book title/author, then
    /// fetches that page's summary extract. Returns null if no reasonably
    /// matching page is found.
    /// </summary>
    Task<string?> GetBookDescriptionAsync(string title, string? author = null);
}
