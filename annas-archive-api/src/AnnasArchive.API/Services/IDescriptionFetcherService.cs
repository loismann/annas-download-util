namespace AnnasArchive.API.Services;

/// <summary>
/// Result from a description fetch operation.
/// </summary>
/// <param name="Description">The fetched description, or null if not found</param>
/// <param name="Source">The source of the description (e.g., "Google Books", "Open Library", "GPT-4")</param>
public record DescriptionFetchResult(string? Description, string? Source);

/// <summary>
/// Service for fetching book descriptions from multiple sources with cascading fallback.
/// Consolidates the common pattern of trying Google Books -> Open Library -> GPT-4.
/// </summary>
public interface IDescriptionFetcherService
{
    /// <summary>
    /// Fetches a book description using a cascade of sources:
    /// 1. Google Books API
    /// 2. Open Library API
    /// 3. GPT-4 (AI-generated, if enabled)
    /// </summary>
    /// <param name="title">The book title</param>
    /// <param name="author">The book author (optional)</param>
    /// <param name="isbn">The book ISBN (optional, improves accuracy)</param>
    /// <param name="includeAiFallback">Whether to include AI-generated description as fallback (default: true)</param>
    /// <param name="useDeepModel">Whether the AI fallback should use the "deep" model instead of the default "fast" one (default: false)</param>
    /// <returns>A result containing the description and its source</returns>
    Task<DescriptionFetchResult> FetchDescriptionAsync(
        string title,
        string? author = null,
        string? isbn = null,
        bool includeAiFallback = true,
        bool useDeepModel = false,
        string? billTo = null);

    /// <summary>
    /// Fetches a book description from Google Books only.
    /// </summary>
    Task<DescriptionFetchResult> FetchFromGoogleBooksAsync(string title, string? author = null, string? isbn = null);

    /// <summary>
    /// Fetches a book description from Open Library only.
    /// </summary>
    Task<DescriptionFetchResult> FetchFromOpenLibraryAsync(string title, string? author = null, string? isbn = null);

    /// <summary>
    /// Generates a book description using AI (the "fast" model by default, or the "deep" model when requested).
    /// </summary>
    /// <param name="billTo">Owner key to charge for the AI call, or
    /// <c>AiSpend.BackgroundAccount</c>. Null records nothing — this path used to
    /// have no way to say who was spending, so it recorded nothing at all.</param>
    Task<DescriptionFetchResult> FetchFromAiAsync(
        string title, string? author = null, bool useDeepModel = false, string? billTo = null);
}
