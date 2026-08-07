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

    /// <summary>
    /// Timeout for proxied media streaming calls (audiobook audio/cover via
    /// Audiobookshelf) — applies to the whole request including reading the
    /// response body, so it needs to comfortably outlast a single range-chunk
    /// transfer over a slow connection, not just a metadata round-trip
    /// (30 minutes).
    /// </summary>
    public static readonly TimeSpan MediaStreamingTimeout = TimeSpan.FromMinutes(30);

    // ========================================================================
    // Quick Operation Timeouts
    // ========================================================================

    /// <summary>
    /// Timeout for Sonarr/Radarr operations (60 seconds). Interactive release
    /// searches fan out to indexers and routinely take longer than a metadata
    /// lookup even though the *arr API itself is on the local Docker network.
    /// </summary>
    public static readonly TimeSpan ArrOperationTimeout = TimeSpan.FromSeconds(60);

    // ========================================================================
    // Cache Timeouts
    // ========================================================================

    /// <summary>
    /// Timeout for OpenLibrary cache lookups (3 seconds).
    /// </summary>
    public static readonly TimeSpan OpenLibraryCacheLookup = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Short timeout for scraper operations with fallback (4 seconds).
    /// </summary>
    public static readonly TimeSpan ShortScraperTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Timeout for library HTTP operations like file downloads (6 seconds).
    /// </summary>
    public static readonly TimeSpan LibraryHttpOperation = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Cache TTL for author data (6 hours).
    /// </summary>
    public static readonly TimeSpan AuthorCacheTtl = TimeSpan.FromHours(6);
}
