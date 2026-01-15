namespace AnnasArchive.API.Constants;

/// <summary>
/// Limit constants for pagination, rate limiting, and data constraints.
/// Centralized to ensure consistency across the application.
/// </summary>
public static class Limits
{
    // ========================================================================
    // Rate Limiting
    // ========================================================================

    /// <summary>
    /// Default API rate limit per minute per IP.
    /// </summary>
    public const int DefaultApiRateLimit = 60;

    /// <summary>
    /// Login attempt rate limit per minute per IP.
    /// </summary>
    public const int LoginRateLimit = 5;

    // ========================================================================
    // Search & Pagination
    // ========================================================================

    /// <summary>
    /// Default search result limit.
    /// </summary>
    public const int DefaultSearchLimit = 50;

    /// <summary>
    /// Maximum detail fetches per search (for ISBN/cover lookups).
    /// </summary>
    public const int MaxDetailFetches = 5;

    /// <summary>
    /// Concurrent request limit for parallel operations.
    /// </summary>
    public const int ConcurrencyLimit = 6;

    // ========================================================================
    // Content Limits
    // ========================================================================

    /// <summary>
    /// Maximum request body size in bytes (20 MB).
    /// </summary>
    public const int MaxRequestBodySize = 20 * 1024 * 1024;

    /// <summary>
    /// Maximum cover candidates to return from lookup.
    /// </summary>
    public const int MaxCoverCandidates = 12;

    /// <summary>
    /// Maximum known words to use for vocabulary analysis.
    /// </summary>
    public const int MaxKnownWordsForAnalysis = 100;

    // ========================================================================
    // Download Tracking
    // ========================================================================

    /// <summary>
    /// Default download limit per rolling window.
    /// </summary>
    public const int DefaultDownloadLimit = 50;

    /// <summary>
    /// Default rolling window for download tracking in hours.
    /// </summary>
    public const double DefaultDownloadWindowHours = 18;

    // ========================================================================
    // AI Processing
    // ========================================================================

    /// <summary>
    /// Batch size for AI processing operations.
    /// </summary>
    public const int AiBatchSize = 5;

    /// <summary>
    /// Maximum tokens per AI request.
    /// </summary>
    public const int MaxTokensPerRequest = 4096;
}
