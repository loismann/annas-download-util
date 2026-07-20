namespace AnnasArchive.API.Configuration;

/// <summary>
/// Centralized throttling configuration for all API calls (AI, external services, background processing).
/// Prevents rate limiting by adding delays between sequential calls.
/// </summary>
public static class AiThrottlingConfiguration
{
    #region Shared Throttling Values

    /// <summary>
    /// Delay between individual API calls within a batch operation.
    /// Prevents hitting per-minute rate limits (RPM).
    /// Used by: AI endpoints, LibraryWatcher
    /// </summary>
    public static readonly TimeSpan DelayBetweenApiCalls = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Delay between processing items in a loop (e.g., chunks, sections).
    /// Longer delay to allow rate limit recovery.
    /// Used by: AI chunk/section summaries
    /// </summary>
    public static readonly TimeSpan DelayBetweenItems = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum number of items to process before taking a longer pause.
    /// Used by: AI endpoints, LibraryWatcher
    /// </summary>
    public static readonly int BatchSize = 10;

    /// <summary>
    /// Maximum number of related books to fetch descriptions for.
    /// Prevents cascading API calls for large series.
    /// </summary>
    public static readonly int MaxRelatedBookDescriptions = 8;

    /// <summary>
    /// Maximum concurrent AI operations per user.
    /// Prevents a single user from overwhelming the API.
    /// </summary>
    public static readonly int MaxConcurrentOperationsPerUser = 3;

    #endregion

    #region AI Endpoint Throttling

    /// <summary>
    /// Delay between batches for AI operations (chunk/section summaries).
    /// Shorter delay since these are user-facing and need responsiveness.
    /// </summary>
    public static readonly TimeSpan AiDelayBetweenBatches = TimeSpan.FromSeconds(5);

    #endregion

    #region Library Watcher Throttling

    /// <summary>
    /// Interval between full library scans.
    /// Daily scans to reduce API load while keeping metadata fresh.
    /// </summary>
    public static readonly TimeSpan LibraryScanInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Debounce window for file system events.
    /// Prevents processing the same file multiple times during rapid changes.
    /// </summary>
    public static readonly TimeSpan LibraryDebounceWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Delay between processing individual books in the library.
    /// Each book may trigger up to 4 API calls (OpenLibrary, AI, GoogleBooks, Goodreads).
    /// </summary>
    public static readonly TimeSpan LibraryDelayBetweenBooks = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delay between batches for LibraryWatcher.
    /// Longer delay since this is background processing and can afford to be slower.
    /// </summary>
    public static readonly TimeSpan LibraryDelayBetweenBatches = TimeSpan.FromSeconds(10);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Helper method to add throttling delay between API calls.
    /// </summary>
    public static Task ThrottleAsync(CancellationToken token = default)
    {
        return Task.Delay(DelayBetweenApiCalls, token);
    }

    /// <summary>
    /// Helper method to add longer throttling delay between items.
    /// </summary>
    public static Task ThrottleBetweenItemsAsync(CancellationToken token = default)
    {
        return Task.Delay(DelayBetweenItems, token);
    }

    /// <summary>
    /// Helper method to add batch pause delay for AI operations.
    /// </summary>
    public static Task ThrottleBetweenBatchesAsync(CancellationToken token = default)
    {
        return Task.Delay(AiDelayBetweenBatches, token);
    }

    /// <summary>
    /// Helper method to add delay between books in library processing.
    /// </summary>
    public static Task ThrottleBetweenBooksAsync(CancellationToken token = default)
    {
        return Task.Delay(LibraryDelayBetweenBooks, token);
    }

    /// <summary>
    /// Helper method to add batch pause delay for library processing.
    /// </summary>
    public static Task ThrottleLibraryBatchAsync(CancellationToken token = default)
    {
        return Task.Delay(LibraryDelayBetweenBatches, token);
    }

    #endregion
}
