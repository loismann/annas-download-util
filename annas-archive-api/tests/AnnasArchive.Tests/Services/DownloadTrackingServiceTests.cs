using AnnasArchive.Core.Services;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class DownloadTrackingServiceTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly DownloadTrackingService _service;

    public DownloadTrackingServiceTests()
    {
        // Use a temporary file for testing
        _testFilePath = Path.Combine(Path.GetTempPath(), $"test-download-tracking-{Guid.NewGuid()}.json");
        _service = new DownloadTrackingService(downloadLimit: 5, rollingWindowHours: 18, storagePath: _testFilePath);
    }

    public void Dispose()
    {
        // Clean up test file
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    #region Basic Tracking Tests

    [Fact]
    public void GetDownloadStatus_NewService_ShouldReturnFullLimit()
    {
        // Act
        var (downloadsLeft, downloadsPerDay) = _service.GetDownloadStatus();

        // Assert
        Assert.Equal(5, downloadsLeft);
        Assert.Equal(5, downloadsPerDay);
    }

    [Fact]
    public void RecordDownload_SingleDownload_ShouldDecrementRemaining()
    {
        // Act
        _service.RecordDownload("abc123", "test@example.com");
        var (downloadsLeft, downloadsPerDay) = _service.GetDownloadStatus();

        // Assert
        Assert.Equal(4, downloadsLeft);
        Assert.Equal(5, downloadsPerDay);
    }

    [Fact]
    public void RecordDownload_MultipleDownloads_ShouldDecrementCorrectly()
    {
        // Act
        _service.RecordDownload("md5-1", "user@example.com");
        _service.RecordDownload("md5-2", "user@example.com");
        _service.RecordDownload("md5-3", "user@example.com");
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert
        Assert.Equal(2, downloadsLeft);
    }

    [Fact]
    public void RecordDownload_AtLimit_ShouldReturnZeroRemaining()
    {
        // Act
        for (int i = 0; i < 5; i++)
        {
            _service.RecordDownload($"md5-{i}", "user@example.com");
        }
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert
        Assert.Equal(0, downloadsLeft);
    }

    [Fact]
    public void RecordDownload_OverLimit_ShouldNotGoBelowZero()
    {
        // Act
        for (int i = 0; i < 10; i++)
        {
            _service.RecordDownload($"md5-{i}", "user@example.com");
        }
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert
        Assert.Equal(0, downloadsLeft);
    }

    [Fact]
    public void GetDownloadLimit_ShouldReturnConfiguredLimit()
    {
        // Act
        var limit = _service.GetDownloadLimit();

        // Assert
        Assert.Equal(5, limit);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public void Persistence_ShouldSaveAndLoadAcrossInstances()
    {
        // Arrange
        _service.RecordDownload("md5-1", "user@example.com");
        _service.RecordDownload("md5-2", "user@example.com");

        // Act - Create new instance with same file
        var newService = new DownloadTrackingService(downloadLimit: 5, storagePath: _testFilePath);
        var (downloadsLeft, _) = newService.GetDownloadStatus();

        // Assert
        Assert.Equal(3, downloadsLeft);
    }

    [Fact]
    public void Persistence_DifferentUsers_ShouldShareLimit()
    {
        // Act - Multiple users download
        _service.RecordDownload("md5-1", "user1@example.com");
        _service.RecordDownload("md5-2", "user2@example.com");
        _service.RecordDownload("md5-3", "user3@example.com");

        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert - All count against the same limit
        Assert.Equal(2, downloadsLeft);
    }

    #endregion

    #region Rolling Window Tests

    [Fact]
    public void RollingWindow_OldDownloads_ShouldBeCleanedUp()
    {
        // Arrange - Create a service with very short window for testing
        // 0.00001 hours = 0.036 seconds = 36 milliseconds
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-window-{Guid.NewGuid()}.json");
        var shortWindowService = new DownloadTrackingService(
            downloadLimit: 5,
            rollingWindowHours: 0.00001, // ~36 milliseconds
            storagePath: testFilePath);

        try
        {
            // Record download
            shortWindowService.RecordDownload("md5-1", "user@example.com");
            var (before, _) = shortWindowService.GetDownloadStatus();
            Assert.Equal(4, before);

            // Wait for window to expire (100ms > 36ms window)
            Thread.Sleep(100);

            // Act - Get status again (should trigger cleanup)
            var (after, _) = shortWindowService.GetDownloadStatus();

            // Assert - Download should be cleaned up
            Assert.Equal(5, after);
        }
        finally
        {
            if (File.Exists(testFilePath))
                File.Delete(testFilePath);
        }
    }

    [Fact]
    public void RollingWindow_RecentDownloads_ShouldNotBeCleanedUp()
    {
        // Arrange - Create a service with long window
        var longWindowService = new DownloadTrackingService(
            downloadLimit: 5,
            rollingWindowHours: 24, // 24 hours
            storagePath: Path.Combine(Path.GetTempPath(), $"test-long-window-{Guid.NewGuid()}.json"));

        try
        {
            // Record downloads
            longWindowService.RecordDownload("md5-1", "user@example.com");
            longWindowService.RecordDownload("md5-2", "user@example.com");

            // Wait briefly
            Thread.Sleep(100);

            // Act
            var (downloadsLeft, _) = longWindowService.GetDownloadStatus();

            // Assert - Downloads should still count
            Assert.Equal(3, downloadsLeft);
        }
        finally
        {
            var testFiles = Directory.GetFiles(Path.GetTempPath(), "test-long-window-*.json");
            foreach (var file in testFiles)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    [Fact]
    public void RollingWindow_MixedAgeDownloads_ShouldOnlyCountRecent()
    {
        // Arrange - Directly manipulate the tracking file to simulate old downloads
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Downloads = new[]
            {
                new { Timestamp = DateTime.UtcNow.AddHours(-20), Md5 = "old-1", UserEmail = "user@example.com" }, // Outside window
                new { Timestamp = DateTime.UtcNow.AddHours(-19), Md5 = "old-2", UserEmail = "user@example.com" }, // Outside window
                new { Timestamp = DateTime.UtcNow.AddHours(-1), Md5 = "recent-1", UserEmail = "user@example.com" }, // Inside window
                new { Timestamp = DateTime.UtcNow, Md5 = "recent-2", UserEmail = "user@example.com" } // Inside window
            }
        });

        File.WriteAllText(_testFilePath, json);

        // Act - Create new service instance to load the data
        var service = new DownloadTrackingService(downloadLimit: 5, rollingWindowHours: 18, storagePath: _testFilePath);
        var (downloadsLeft, _) = service.GetDownloadStatus();

        // Assert - Only recent downloads should count (2 inside the 18-hour window)
        Assert.Equal(3, downloadsLeft); // 5 - 2 = 3
    }

    #endregion

    #region Custom Configuration Tests

    [Fact]
    public void CustomLimit_ShouldUseConfiguredValue()
    {
        // Arrange
        var customService = new DownloadTrackingService(
            downloadLimit: 100,
            storagePath: Path.Combine(Path.GetTempPath(), $"test-custom-{Guid.NewGuid()}.json"));

        try
        {
            // Act
            var limit = customService.GetDownloadLimit();
            var (downloadsLeft, downloadsPerDay) = customService.GetDownloadStatus();

            // Assert
            Assert.Equal(100, limit);
            Assert.Equal(100, downloadsLeft);
            Assert.Equal(100, downloadsPerDay);
        }
        finally
        {
            var testFiles = Directory.GetFiles(Path.GetTempPath(), "test-custom-*.json");
            foreach (var file in testFiles)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public void ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        const int threadCount = 5;
        var tasks = new List<Task>();

        // Act - Multiple threads recording downloads
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks.Add(Task.Run(() =>
            {
                _service.RecordDownload($"md5-thread-{threadId}", $"user{threadId}@example.com");
            }));
        }

        Task.WaitAll(tasks.ToArray());
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert - All downloads should be recorded
        Assert.Equal(0, downloadsLeft); // 5 downloads against limit of 5
    }

    [Fact]
    public void ConcurrentAccess_MultipleReadsAndWrites_ShouldBeConsistent()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act - Mix of reads and writes
        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
            {
                tasks.Add(Task.Run(() => _service.RecordDownload($"md5-{Guid.NewGuid()}", "user@example.com")));
            }
            else
            {
                tasks.Add(Task.Run(() => _service.GetDownloadStatus()));
            }
        }

        // Should not throw any exceptions
        Task.WaitAll(tasks.ToArray());

        // Assert - Should complete without error
        var (downloadsLeft, _) = _service.GetDownloadStatus();
        Assert.True(downloadsLeft >= 0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void RecordDownload_EmptyMd5_ShouldStillRecord()
    {
        // Act
        _service.RecordDownload("", "user@example.com");
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert - Should still count against limit
        Assert.Equal(4, downloadsLeft);
    }

    [Fact]
    public void RecordDownload_EmptyEmail_ShouldStillRecord()
    {
        // Act
        _service.RecordDownload("md5-123", "");
        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert - Should still count against limit
        Assert.Equal(4, downloadsLeft);
    }

    [Fact]
    public void RecordDownload_SameMd5MultipleTimes_ShouldCountEachDownload()
    {
        // Act - Download same book multiple times
        _service.RecordDownload("same-md5", "user@example.com");
        _service.RecordDownload("same-md5", "user@example.com");
        _service.RecordDownload("same-md5", "user@example.com");

        var (downloadsLeft, _) = _service.GetDownloadStatus();

        // Assert - Each download counts
        Assert.Equal(2, downloadsLeft);
    }

    #endregion
}
