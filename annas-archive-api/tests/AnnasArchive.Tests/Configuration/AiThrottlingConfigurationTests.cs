using AnnasArchive.API.Configuration;
using Xunit;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// Tests to validate AI throttling configuration values are within acceptable ranges.
/// These ensure rate-limiting protection stays effective.
/// </summary>
public class AiThrottlingConfigurationTests
{
    #region Shared Throttling Values

    [Fact]
    public void DelayBetweenApiCalls_ShouldBeAtLeast100ms()
    {
        // Assert - Should be at least 100ms to spread API load
        Assert.True(AiThrottlingConfiguration.DelayBetweenApiCalls >= TimeSpan.FromMilliseconds(100),
            $"DelayBetweenApiCalls should be at least 100ms, but was {AiThrottlingConfiguration.DelayBetweenApiCalls.TotalMilliseconds}ms");
    }

    [Fact]
    public void DelayBetweenApiCalls_ShouldNotExceed2Seconds()
    {
        // Assert - Should not be so long it makes the app feel sluggish
        Assert.True(AiThrottlingConfiguration.DelayBetweenApiCalls <= TimeSpan.FromSeconds(2),
            $"DelayBetweenApiCalls should not exceed 2 seconds, but was {AiThrottlingConfiguration.DelayBetweenApiCalls.TotalSeconds}s");
    }

    [Fact]
    public void DelayBetweenItems_ShouldBeAtLeast200ms()
    {
        // Assert - Should allow reasonable recovery between items
        Assert.True(AiThrottlingConfiguration.DelayBetweenItems >= TimeSpan.FromMilliseconds(200),
            $"DelayBetweenItems should be at least 200ms, but was {AiThrottlingConfiguration.DelayBetweenItems.TotalMilliseconds}ms");
    }

    [Fact]
    public void DelayBetweenItems_ShouldNotExceed5Seconds()
    {
        // Assert - Should not be so long it makes processing too slow
        Assert.True(AiThrottlingConfiguration.DelayBetweenItems <= TimeSpan.FromSeconds(5),
            $"DelayBetweenItems should not exceed 5 seconds, but was {AiThrottlingConfiguration.DelayBetweenItems.TotalSeconds}s");
    }

    [Fact]
    public void BatchSize_ShouldBeBetween5And50()
    {
        // Assert - Batch size should be reasonable
        Assert.InRange(AiThrottlingConfiguration.BatchSize, 5, 50);
    }

    [Fact]
    public void MaxRelatedBookDescriptions_ShouldBeBetween3And20()
    {
        // Assert - Should be enough to be useful but not cause rate limits
        Assert.InRange(AiThrottlingConfiguration.MaxRelatedBookDescriptions, 3, 20);
    }

    [Fact]
    public void MaxConcurrentOperationsPerUser_ShouldBeBetween1And10()
    {
        // Assert - Should prevent single user from overwhelming the API
        Assert.InRange(AiThrottlingConfiguration.MaxConcurrentOperationsPerUser, 1, 10);
    }

    #endregion

    #region AI Endpoint Throttling

    [Fact]
    public void AiDelayBetweenBatches_ShouldBeAtLeast1Second()
    {
        // Assert - Should allow rate limits to recover
        Assert.True(AiThrottlingConfiguration.AiDelayBetweenBatches >= TimeSpan.FromSeconds(1),
            $"AiDelayBetweenBatches should be at least 1 second, but was {AiThrottlingConfiguration.AiDelayBetweenBatches.TotalSeconds}s");
    }

    [Fact]
    public void AiDelayBetweenBatches_ShouldNotExceed30Seconds()
    {
        // Assert - Should not be so long it stalls processing for user-facing operations
        Assert.True(AiThrottlingConfiguration.AiDelayBetweenBatches <= TimeSpan.FromSeconds(30),
            $"AiDelayBetweenBatches should not exceed 30 seconds, but was {AiThrottlingConfiguration.AiDelayBetweenBatches.TotalSeconds}s");
    }

    #endregion

    #region Library Watcher Throttling

    [Fact]
    public void LibraryScanInterval_ShouldBeAtLeast1Hour()
    {
        // Assert - Should not scan too frequently
        Assert.True(AiThrottlingConfiguration.LibraryScanInterval >= TimeSpan.FromHours(1),
            $"LibraryScanInterval should be at least 1 hour, but was {AiThrottlingConfiguration.LibraryScanInterval.TotalHours}h");
    }

    [Fact]
    public void LibraryScanInterval_ShouldNotExceed7Days()
    {
        // Assert - Should scan at least weekly to keep metadata fresh
        Assert.True(AiThrottlingConfiguration.LibraryScanInterval <= TimeSpan.FromDays(7),
            $"LibraryScanInterval should not exceed 7 days, but was {AiThrottlingConfiguration.LibraryScanInterval.TotalDays}d");
    }

    [Fact]
    public void LibraryDebounceWindow_ShouldBeAtLeast1Second()
    {
        // Assert - Should allow time for file operations to complete
        Assert.True(AiThrottlingConfiguration.LibraryDebounceWindow >= TimeSpan.FromSeconds(1),
            $"LibraryDebounceWindow should be at least 1 second, but was {AiThrottlingConfiguration.LibraryDebounceWindow.TotalSeconds}s");
    }

    [Fact]
    public void LibraryDebounceWindow_ShouldNotExceed30Seconds()
    {
        // Assert - Should not wait too long before processing new files
        Assert.True(AiThrottlingConfiguration.LibraryDebounceWindow <= TimeSpan.FromSeconds(30),
            $"LibraryDebounceWindow should not exceed 30 seconds, but was {AiThrottlingConfiguration.LibraryDebounceWindow.TotalSeconds}s");
    }

    [Fact]
    public void LibraryDelayBetweenBooks_ShouldBeAtLeast1Second()
    {
        // Assert - Should prevent rate limiting with up to 4 API calls per book
        Assert.True(AiThrottlingConfiguration.LibraryDelayBetweenBooks >= TimeSpan.FromSeconds(1),
            $"LibraryDelayBetweenBooks should be at least 1 second, but was {AiThrottlingConfiguration.LibraryDelayBetweenBooks.TotalSeconds}s");
    }

    [Fact]
    public void LibraryDelayBetweenBooks_ShouldNotExceed10Seconds()
    {
        // Assert - Should not make library processing too slow
        Assert.True(AiThrottlingConfiguration.LibraryDelayBetweenBooks <= TimeSpan.FromSeconds(10),
            $"LibraryDelayBetweenBooks should not exceed 10 seconds, but was {AiThrottlingConfiguration.LibraryDelayBetweenBooks.TotalSeconds}s");
    }

    [Fact]
    public void LibraryDelayBetweenBatches_ShouldBeAtLeast10Seconds()
    {
        // Assert - Should allow rate limits to fully recover between batches
        Assert.True(AiThrottlingConfiguration.LibraryDelayBetweenBatches >= TimeSpan.FromSeconds(10),
            $"LibraryDelayBetweenBatches should be at least 10 seconds, but was {AiThrottlingConfiguration.LibraryDelayBetweenBatches.TotalSeconds}s");
    }

    [Fact]
    public void LibraryDelayBetweenBatches_ShouldNotExceed120Seconds()
    {
        // Assert - Should not make background processing too slow
        Assert.True(AiThrottlingConfiguration.LibraryDelayBetweenBatches <= TimeSpan.FromSeconds(120),
            $"LibraryDelayBetweenBatches should not exceed 120 seconds, but was {AiThrottlingConfiguration.LibraryDelayBetweenBatches.TotalSeconds}s");
    }

    #endregion

    #region Helper Method Tests

    [Fact]
    public async Task ThrottleAsync_ShouldDelayExecution()
    {
        // Arrange
        var start = DateTime.UtcNow;

        // Act
        await AiThrottlingConfiguration.ThrottleAsync();

        // Assert - Should have delayed at least DelayBetweenApiCalls
        var elapsed = DateTime.UtcNow - start;
        Assert.True(elapsed >= AiThrottlingConfiguration.DelayBetweenApiCalls - TimeSpan.FromMilliseconds(50),
            $"ThrottleAsync should delay by at least {AiThrottlingConfiguration.DelayBetweenApiCalls.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleBetweenItemsAsync_ShouldDelayExecution()
    {
        // Arrange
        var start = DateTime.UtcNow;

        // Act
        await AiThrottlingConfiguration.ThrottleBetweenItemsAsync();

        // Assert - Should have delayed at least DelayBetweenItems
        var elapsed = DateTime.UtcNow - start;
        Assert.True(elapsed >= AiThrottlingConfiguration.DelayBetweenItems - TimeSpan.FromMilliseconds(50),
            $"ThrottleBetweenItemsAsync should delay by at least {AiThrottlingConfiguration.DelayBetweenItems.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleBetweenBatchesAsync_ShouldDelayExecution()
    {
        // Arrange
        var start = DateTime.UtcNow;

        // Act
        await AiThrottlingConfiguration.ThrottleBetweenBatchesAsync();

        // Assert - Should have delayed at least AiDelayBetweenBatches
        var elapsed = DateTime.UtcNow - start;
        Assert.True(elapsed >= AiThrottlingConfiguration.AiDelayBetweenBatches - TimeSpan.FromMilliseconds(50),
            $"ThrottleBetweenBatchesAsync should delay by at least {AiThrottlingConfiguration.AiDelayBetweenBatches.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleBetweenBooksAsync_ShouldDelayExecution()
    {
        // Arrange
        var start = DateTime.UtcNow;

        // Act
        await AiThrottlingConfiguration.ThrottleBetweenBooksAsync();

        // Assert - Should have delayed at least LibraryDelayBetweenBooks
        var elapsed = DateTime.UtcNow - start;
        Assert.True(elapsed >= AiThrottlingConfiguration.LibraryDelayBetweenBooks - TimeSpan.FromMilliseconds(50),
            $"ThrottleBetweenBooksAsync should delay by at least {AiThrottlingConfiguration.LibraryDelayBetweenBooks.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_ShouldRespectCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert - Should throw OperationCanceledException
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => AiThrottlingConfiguration.ThrottleAsync(cts.Token));
    }

    [Fact]
    public async Task ThrottleBetweenBooksAsync_ShouldRespectCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert - Should throw OperationCanceledException
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => AiThrottlingConfiguration.ThrottleBetweenBooksAsync(cts.Token));
    }

    #endregion

    #region Rate Limit Prevention Tests

    [Fact]
    public void AiThrottlingValues_ShouldPreventRateLimitBursts()
    {
        // Calculate max API calls per minute with current settings
        // This ensures the configuration actually prevents rate limits
        //
        // AI endpoints (chunk/section summaries) are single-call per item

        var maxCallsPerItem = 1; // Most AI operations are single-call
        var itemsPerBatch = AiThrottlingConfiguration.BatchSize;

        // Time per item = delay between items
        var timePerItem = AiThrottlingConfiguration.DelayBetweenItems;

        // Time per batch = items × time per item + batch delay
        var timePerBatch = (timePerItem * itemsPerBatch) + AiThrottlingConfiguration.AiDelayBetweenBatches;

        // Calculate calls per minute
        var batchesPerMinute = TimeSpan.FromMinutes(1) / timePerBatch;
        var maxCallsPerMinute = batchesPerMinute * itemsPerBatch * maxCallsPerItem;

        // Assert - Should be well under typical rate limits
        Assert.True(maxCallsPerMinute < 120,
            $"Max AI API calls per minute ({maxCallsPerMinute:F1}) should be under 120. " +
            $"Time per batch: {timePerBatch.TotalSeconds:F1}s, items per batch: {itemsPerBatch}");
    }

    [Fact]
    public void LibraryWatcherThrottlingValues_ShouldPreventRateLimitBursts()
    {
        // Calculate max API calls per minute with LibraryWatcher settings
        // Each book can trigger up to 4 API calls (OpenLibrary, AI, GoogleBooks, Goodreads)

        var maxCallsPerBook = 4;
        var booksPerBatch = AiThrottlingConfiguration.BatchSize;

        // Time per book = delay between books + (delay between API calls × max calls)
        var timePerBook = AiThrottlingConfiguration.LibraryDelayBetweenBooks +
                          (AiThrottlingConfiguration.DelayBetweenApiCalls * maxCallsPerBook);

        // Time per batch = books × time per book + batch delay
        var timePerBatch = (timePerBook * booksPerBatch) + AiThrottlingConfiguration.LibraryDelayBetweenBatches;

        // Calculate calls per minute
        var batchesPerMinute = TimeSpan.FromMinutes(1) / timePerBatch;
        var maxCallsPerMinute = batchesPerMinute * booksPerBatch * maxCallsPerBook;

        // Assert - Should be well under typical rate limits (60 RPM for OpenAI)
        Assert.True(maxCallsPerMinute < 60,
            $"Max LibraryWatcher API calls per minute ({maxCallsPerMinute:F1}) should be under 60. " +
            $"Time per batch: {timePerBatch.TotalSeconds:F1}s, books per batch: {booksPerBatch}");
    }

    [Fact]
    public void RelatedBooksThrottling_ShouldLimitDescriptionFetches()
    {
        // Verify that the max descriptions limit is reasonable
        var maxDescriptions = AiThrottlingConfiguration.MaxRelatedBookDescriptions;
        var delayPerCall = AiThrottlingConfiguration.DelayBetweenApiCalls;

        // Time to fetch all descriptions
        var totalTime = delayPerCall * maxDescriptions;

        // Should complete in under 10 seconds
        Assert.True(totalTime < TimeSpan.FromSeconds(10),
            $"Fetching {maxDescriptions} descriptions should take < 10s, but would take {totalTime.TotalSeconds:F1}s");

        // Should not exceed 10 descriptions (prevents cascading for large series)
        Assert.True(maxDescriptions <= 10,
            $"MaxRelatedBookDescriptions ({maxDescriptions}) should not exceed 10");
    }

    #endregion
}
