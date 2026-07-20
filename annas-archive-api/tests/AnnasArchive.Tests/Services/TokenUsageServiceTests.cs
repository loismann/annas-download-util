using AnnasArchive.Core.Services;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class TokenUsageServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly TokenUsageService _service;

    public TokenUsageServiceTests()
    {
        // Use a temporary directory for testing
        _testDirectory = Path.Combine(Path.GetTempPath(), $"test-ai-usage-{Guid.NewGuid()}");
        _service = new TokenUsageService(_testDirectory);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void AddUsage_SingleUser_ShouldIncrementTokens()
    {
        // Arrange
        const string userId = "user-123";

        // Act
        _service.AddUsage(userId, 100, 50);
        var totals = _service.GetTotals(userId);

        // Assert
        Assert.Equal(100, totals.PromptTokens);
        Assert.Equal(50, totals.CompletionTokens);
        Assert.Equal(150, totals.TotalTokens);
    }

    [Fact]
    public void AddUsage_MultipleTimes_ShouldAccumulate()
    {
        // Arrange
        const string userId = "user-456";

        // Act
        _service.AddUsage(userId, 100, 50);
        _service.AddUsage(userId, 200, 75);
        _service.AddUsage(userId, 50, 25);

        var totals = _service.GetTotals(userId);

        // Assert
        Assert.Equal(350, totals.PromptTokens);
        Assert.Equal(150, totals.CompletionTokens);
        Assert.Equal(500, totals.TotalTokens);
    }

    [Fact]
    public void AddUsage_MultipleUsers_ShouldTrackIndependently()
    {
        // Arrange
        const string user1 = "user-dad";
        const string user2 = "user-mom";
        const string user3 = "user-paul";

        // Act
        _service.AddUsage(user1, 1000, 500);
        _service.AddUsage(user2, 2000, 1000);
        _service.AddUsage(user3, 500, 250);

        var totals1 = _service.GetTotals(user1);
        var totals2 = _service.GetTotals(user2);
        var totals3 = _service.GetTotals(user3);

        // Assert
        Assert.Equal(1000, totals1.PromptTokens);
        Assert.Equal(2000, totals2.PromptTokens);
        Assert.Equal(500, totals3.PromptTokens);
    }

    [Fact]
    public void GetTotals_NewUser_ShouldReturnZeros()
    {
        // Act
        var totals = _service.GetTotals("new-user");

        // Assert
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(0, totals.TotalTokens);
    }

    [Fact]
    public void GetAllUsersUsage_MultipleUsers_ShouldReturnAllUsers()
    {
        // Arrange
        _service.AddUsage("user-1", 1000, 500);
        _service.AddUsage("user-2", 2000, 1000);
        _service.AddUsage("user-3", 3000, 1500);

        // Act
        var allUsage = _service.GetAllUsersUsage();

        // Assert
        Assert.Equal(3, allUsage.Count);
        Assert.True(allUsage.ContainsKey("user-1"));
        Assert.True(allUsage.ContainsKey("user-2"));
        Assert.True(allUsage.ContainsKey("user-3"));

        Assert.Equal(1000, allUsage["user-1"].PromptTokens);
        Assert.Equal(2000, allUsage["user-2"].PromptTokens);
        Assert.Equal(3000, allUsage["user-3"].PromptTokens);
    }

    [Fact]
    public void GetAllUsersUsage_NoUsers_ShouldReturnEmpty()
    {
        // Act
        var allUsage = _service.GetAllUsersUsage();

        // Assert
        Assert.Empty(allUsage);
    }

    [Fact]
    public void CalculateCostUsd_ShouldUseCorrectPricing()
    {
        // GPT-5.2 pricing: $5 per 1M input, $15 per 1M output
        // 1,000,000 prompt + 1,000,000 completion = $5 + $15 = $20

        // Act
        var cost = _service.CalculateCostUsd(1_000_000, 1_000_000);

        // Assert
        Assert.Equal(20.0, cost);
    }

    [Fact]
    public void CalculateCostUsd_SmallNumbers_ShouldWork()
    {
        // 10,000 prompt + 5,000 completion
        // = (10,000 / 1M * $5) + (5,000 / 1M * $15)
        // = $0.05 + $0.075 = $0.125 → rounds to $0.12

        // Act
        var cost = _service.CalculateCostUsd(10_000, 5_000);

        // Assert
        Assert.Equal(0.12, cost, precision: 2);
    }

    [Fact]
    public void CalculateCostUsd_RealWorldExample_ShouldWork()
    {
        // Realistic usage: 50,000 input + 30,000 output tokens
        // = (50,000 / 1M * $5) + (30,000 / 1M * $15)
        // = $0.25 + $0.45 = $0.70

        // Act
        var cost = _service.CalculateCostUsd(50_000, 30_000);

        // Assert
        Assert.Equal(0.70, cost, precision: 2);
    }

    [Fact]
    public void IsOverLimit_UnderLimit_ShouldReturnFalse()
    {
        // Arrange
        const string userId = "user-test";
        _service.AddUsage(userId, 1000, 500);

        // Act
        var isOver = _service.IsOverLimit(userId, 2000);

        // Assert
        Assert.False(isOver);
    }

    [Fact]
    public void IsOverLimit_AtLimit_ShouldReturnTrue()
    {
        // Arrange
        const string userId = "user-test";
        _service.AddUsage(userId, 1000, 500);

        // Act
        var isOver = _service.IsOverLimit(userId, 1500);

        // Assert
        Assert.True(isOver);
    }

    [Fact]
    public void IsOverLimit_OverLimit_ShouldReturnTrue()
    {
        // Arrange
        const string userId = "user-test";
        _service.AddUsage(userId, 1000, 500);

        // Act
        var isOver = _service.IsOverLimit(userId, 1000);

        // Assert
        Assert.True(isOver);
    }

    [Fact]
    public void Reset_SpecificUser_ShouldClearOnlyThatUser()
    {
        // Arrange
        const string user1 = "user-1";
        const string user2 = "user-2";
        _service.AddUsage(user1, 5000, 3000);
        _service.AddUsage(user2, 2000, 1000);

        // Act
        _service.Reset(user1);
        var totals1 = _service.GetTotals(user1);
        var totals2 = _service.GetTotals(user2);

        // Assert
        Assert.Equal(0, totals1.PromptTokens);
        Assert.Equal(0, totals1.CompletionTokens);

        // User 2 should be unchanged
        Assert.Equal(2000, totals2.PromptTokens);
        Assert.Equal(1000, totals2.CompletionTokens);
    }

    [Fact]
    public void Reset_AllUsers_ShouldClearAllUsers()
    {
        // Arrange
        _service.AddUsage("user-1", 5000, 3000);
        _service.AddUsage("user-2", 2000, 1000);
        _service.AddUsage("user-3", 8000, 4000);

        // Act
        _service.Reset(userId: null);
        var allUsage = _service.GetAllUsersUsage();

        // Assert
        Assert.Empty(allUsage);
    }

    [Fact]
    public void Persistence_ShouldSaveAndLoadAcrossInstances()
    {
        // Arrange
        const string userId = "persistent-user";
        _service.AddUsage(userId, 12345, 67890);

        // Act - Create new service instance with same directory
        var newService = new TokenUsageService(_testDirectory);
        var totals = newService.GetTotals(userId);

        // Assert
        Assert.Equal(12345, totals.PromptTokens);
        Assert.Equal(67890, totals.CompletionTokens);
        Assert.Equal(80235, totals.TotalTokens);
    }

    [Fact]
    public void Persistence_MultipleUsers_ShouldPersistIndependently()
    {
        // Arrange
        _service.AddUsage("user-a", 1000, 500);
        _service.AddUsage("user-b", 2000, 1000);
        _service.AddUsage("user-c", 3000, 1500);

        // Act - Create new service instance
        var newService = new TokenUsageService(_testDirectory);
        var allUsage = newService.GetAllUsersUsage();

        // Assert
        Assert.Equal(3, allUsage.Count);
        Assert.Equal(1000, allUsage["user-a"].PromptTokens);
        Assert.Equal(2000, allUsage["user-b"].PromptTokens);
        Assert.Equal(3000, allUsage["user-c"].PromptTokens);
    }

    [Fact]
    public void ConcurrentAccess_SingleUser_ShouldBeThreadSafe()
    {
        // Arrange
        const int iterations = 100;
        const int threadsCount = 10;
        const string userId = "concurrent-user";
        var tasks = new List<Task>();

        // Act - Multiple threads adding usage concurrently for same user
        for (int t = 0; t < threadsCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    _service.AddUsage(userId, 10, 5);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        var totals = _service.GetTotals(userId);

        // Assert - Should have exact total (no race conditions)
        var expectedPrompt = threadsCount * iterations * 10;
        var expectedCompletion = threadsCount * iterations * 5;
        Assert.Equal(expectedPrompt, totals.PromptTokens);
        Assert.Equal(expectedCompletion, totals.CompletionTokens);
    }

    [Fact]
    public void ConcurrentAccess_MultipleUsers_ShouldBeThreadSafe()
    {
        // Arrange
        const int iterations = 50;
        var users = new[] { "user-1", "user-2", "user-3" };
        var tasks = new List<Task>();

        // Act - Multiple threads adding usage for different users
        foreach (var userId in users)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    _service.AddUsage(userId, 10, 5);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - Each user should have exact totals
        foreach (var userId in users)
        {
            var totals = _service.GetTotals(userId);
            Assert.Equal(iterations * 10, totals.PromptTokens);
            Assert.Equal(iterations * 5, totals.CompletionTokens);
        }
    }

    [Fact]
    public void UserIdSanitization_ShouldHandleSpecialCharacters()
    {
        // Arrange
        const string userId = "user/with\\special:characters";

        // Act
        _service.AddUsage(userId, 100, 50);
        var totals = _service.GetTotals(userId);

        // Assert - Should work without errors
        Assert.Equal(100, totals.PromptTokens);
        Assert.Equal(50, totals.CompletionTokens);
    }

    [Fact]
    public void AutoReset_ShouldIncludeLastResetDate()
    {
        // Arrange
        const string userId = "test-user";
        _service.AddUsage(userId, 1000, 500);

        // Act
        var allUsage = _service.GetAllUsersUsage();

        // Assert
        Assert.True(allUsage.ContainsKey(userId));
        var (_, _, _, lastResetDate) = allUsage[userId];
        Assert.True(lastResetDate <= DateTime.UtcNow);
        Assert.True(lastResetDate > DateTime.UtcNow.AddHours(-1)); // Should be very recent
    }

    #region Month Boundary Tests

    [Fact]
    public void MonthBoundary_WhenUsageFromPreviousMonth_ShouldAutoReset()
    {
        // Arrange - Create a file with last month's date
        const string userId = "month-boundary-user";
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 50000L,
            CompletionTokens = 25000L,
            LastResetDate = lastMonth
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // Act - GetTotals should trigger auto-reset
        var totals = _service.GetTotals(userId);

        // Assert - Should be reset to zero
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(0, totals.TotalTokens);
    }

    [Fact]
    public void MonthBoundary_WhenUsageFromCurrentMonth_ShouldNotReset()
    {
        // Arrange - Create a file with current month's date
        const string userId = "current-month-user";
        var currentMonth = DateTime.UtcNow;
        var usageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 50000L,
            CompletionTokens = 25000L,
            LastResetDate = currentMonth
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, usageJson);

        // Act
        var totals = _service.GetTotals(userId);

        // Assert - Should preserve values
        Assert.Equal(50000, totals.PromptTokens);
        Assert.Equal(25000, totals.CompletionTokens);
        Assert.Equal(75000, totals.TotalTokens);
    }

    [Fact]
    public void MonthBoundary_YearRollover_ShouldAutoReset()
    {
        // Arrange - Create a file with last year's date (December)
        const string userId = "year-rollover-user";
        var lastYear = new DateTime(DateTime.UtcNow.Year - 1, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 100000L,
            CompletionTokens = 50000L,
            LastResetDate = lastYear
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // Act - GetTotals should trigger auto-reset
        var totals = _service.GetTotals(userId);

        // Assert - Should be reset to zero (different year)
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(0, totals.TotalTokens);
    }

    [Fact]
    public void MonthBoundary_SameYearDifferentMonth_ShouldAutoReset()
    {
        // Arrange - Create a file from 2 months ago but same year
        const string userId = "same-year-diff-month-user";
        var twoMonthsAgo = DateTime.UtcNow.AddMonths(-2);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 75000L,
            CompletionTokens = 35000L,
            LastResetDate = twoMonthsAgo
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // Act
        var totals = _service.GetTotals(userId);

        // Assert - Should be reset
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
    }

    [Fact]
    public void MonthBoundary_IsOverLimit_ShouldCheckAfterAutoReset()
    {
        // Arrange - Create a file with high usage from last month
        const string userId = "over-limit-user";
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 500000L,
            CompletionTokens = 250000L,
            LastResetDate = lastMonth
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // Act - Check if over limit (should auto-reset first)
        var isOver = _service.IsOverLimit(userId, 100000);

        // Assert - Should NOT be over limit because usage was reset
        Assert.False(isOver);
    }

    [Fact]
    public void MonthBoundary_GetAllUsersUsage_ShouldAutoResetAllStaleUsers()
    {
        // Arrange - Create files with various dates
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var currentMonth = DateTime.UtcNow;

        // Old user from last month
        var oldUserJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 50000L,
            CompletionTokens = 25000L,
            LastResetDate = lastMonth
        });
        File.WriteAllText(Path.Combine(_testDirectory, "old-user.json"), oldUserJson);

        // Current user from this month
        var currentUserJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 30000L,
            CompletionTokens = 15000L,
            LastResetDate = currentMonth
        });
        File.WriteAllText(Path.Combine(_testDirectory, "current-user.json"), currentUserJson);

        // Act
        var allUsage = _service.GetAllUsersUsage();

        // Assert
        Assert.Equal(2, allUsage.Count);

        // Old user should be reset to zero
        Assert.Equal(0, allUsage["old-user"].PromptTokens);
        Assert.Equal(0, allUsage["old-user"].CompletionTokens);

        // Current user should retain values
        Assert.Equal(30000, allUsage["current-user"].PromptTokens);
        Assert.Equal(15000, allUsage["current-user"].CompletionTokens);
    }

    [Fact]
    public void MonthBoundary_AddUsage_ShouldWorkAfterAutoReset()
    {
        // Arrange - Create a file with last month's usage
        const string userId = "add-after-reset-user";
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 100000L,
            CompletionTokens = 50000L,
            LastResetDate = lastMonth
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // First call triggers auto-reset
        var initialTotals = _service.GetTotals(userId);
        Assert.Equal(0, initialTotals.TotalTokens);

        // Act - Add new usage after reset
        _service.AddUsage(userId, 1000, 500);
        var newTotals = _service.GetTotals(userId);

        // Assert - Should only have new usage
        Assert.Equal(1000, newTotals.PromptTokens);
        Assert.Equal(500, newTotals.CompletionTokens);
        Assert.Equal(1500, newTotals.TotalTokens);
    }

    [Fact]
    public void MonthBoundary_ResetDate_ShouldUpdateAfterAutoReset()
    {
        // Arrange - Create a file with old date
        const string userId = "reset-date-user";
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        var oldUsageJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            PromptTokens = 50000L,
            CompletionTokens = 25000L,
            LastResetDate = lastMonth
        });

        var filePath = Path.Combine(_testDirectory, $"{userId}.json");
        File.WriteAllText(filePath, oldUsageJson);

        // Act - Trigger auto-reset
        _service.GetTotals(userId);
        var allUsage = _service.GetAllUsersUsage();

        // Assert - LastResetDate should be updated to current month
        var (_, _, _, lastResetDate) = allUsage[userId];
        Assert.Equal(DateTime.UtcNow.Year, lastResetDate.Year);
        Assert.Equal(DateTime.UtcNow.Month, lastResetDate.Month);
    }

    #endregion
}
