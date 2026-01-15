namespace AnnasArchive.API.Constants;

/// <summary>
/// HTTP timeout constants for external service calls.
/// Centralized to ensure consistency across the application.
/// </summary>
public static class HttpTimeouts
{
    // ========================================================================
    // External API Timeouts
    // ========================================================================

    /// <summary>
    /// Timeout for scraping services like Anna's Archive and LibGen (15 seconds).
    /// Shorter timeout since these have domain fallback.
    /// </summary>
    public static readonly TimeSpan ScrapingTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Timeout for standard external APIs like Google Books and OpenLibrary (30 seconds).
    /// </summary>
    public static readonly TimeSpan StandardApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for AI/LLM operations which can take much longer (5 minutes).
    /// </summary>
    public static readonly TimeSpan AiOperationTimeout = TimeSpan.FromMinutes(5);

    // ========================================================================
    // Quick Operation Timeouts
    // ========================================================================

    /// <summary>
    /// Timeout for quick HTTP operations like cover downloads (5 seconds).
    /// </summary>
    public static readonly TimeSpan QuickOperationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for metadata lookups (10 seconds).
    /// </summary>
    public static readonly TimeSpan MetadataLookupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for title extraction from URLs (12 seconds).
    /// </summary>
    public static readonly TimeSpan TitleExtractionTimeout = TimeSpan.FromSeconds(12);

    // ========================================================================
    // Cache Timeouts
    // ========================================================================

    /// <summary>
    /// Sliding expiration for cached content (30 minutes).
    /// </summary>
    public static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Cache TTL for detail fetches like ISBN lookups (12 hours).
    /// </summary>
    public static readonly TimeSpan DetailCacheTtl = TimeSpan.FromHours(12);
}
