using AnnasArchive.Core.Services;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class TokenUsageServiceTests : IDisposable
{
    private readonly string _testFilePath;
    private readonly TokenUsageService _service;

    public TokenUsageServiceTests()
    {
        // Use a temporary file for testing
        _testFilePath = Path.Combine(Path.GetTempPath(), $"test-usage-{Guid.NewGuid()}.json");
        _service = new TokenUsageService(_testFilePath);
    }

    public void Dispose()
    {
        // Clean up test file
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    [Fact]
    public void AddUsage_ShouldIncrementTokens()
    {
        // Act
        _service.AddUsage(100, 50);
        var totals = _service.GetTotals();

        // Assert
        Assert.Equal(100, totals.PromptTokens);
        Assert.Equal(50, totals.CompletionTokens);
        Assert.Equal(150, totals.TotalTokens);
    }

    [Fact]
    public void AddUsage_MultipleTimes_ShouldAccumulate()
    {
        // Act
        _service.AddUsage(100, 50);
        _service.AddUsage(200, 75);
        _service.AddUsage(50, 25);

        var totals = _service.GetTotals();

        // Assert
        Assert.Equal(350, totals.PromptTokens);
        Assert.Equal(150, totals.CompletionTokens);
        Assert.Equal(500, totals.TotalTokens);
    }

    [Fact]
    public void GetTotals_NewService_ShouldReturnZeros()
    {
        // Act
        var totals = _service.GetTotals();

        // Assert
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(0, totals.TotalTokens);
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
    public void IsOverLimit_UnderLimit_ShouldReturnFalse()
    {
        // Arrange
        _service.AddUsage(1000, 500);

        // Act
        var isOver = _service.IsOverLimit(2000);

        // Assert
        Assert.False(isOver);
    }

    [Fact]
    public void IsOverLimit_AtLimit_ShouldReturnTrue()
    {
        // Arrange
        _service.AddUsage(1000, 500);

        // Act
        var isOver = _service.IsOverLimit(1500);

        // Assert
        Assert.True(isOver);
    }

    [Fact]
    public void IsOverLimit_OverLimit_ShouldReturnTrue()
    {
        // Arrange
        _service.AddUsage(1000, 500);

        // Act
        var isOver = _service.IsOverLimit(1000);

        // Assert
        Assert.True(isOver);
    }

    [Fact]
    public void Reset_ShouldClearAllTokens()
    {
        // Arrange
        _service.AddUsage(5000, 3000);

        // Act
        _service.Reset();
        var totals = _service.GetTotals();

        // Assert
        Assert.Equal(0, totals.PromptTokens);
        Assert.Equal(0, totals.CompletionTokens);
        Assert.Equal(0, totals.TotalTokens);
    }

    [Fact]
    public void Persistence_ShouldSaveAndLoadAcrossInstances()
    {
        // Arrange
        _service.AddUsage(12345, 67890);

        // Act - Create new service instance with same file
        var newService = new TokenUsageService(_testFilePath);
        var totals = newService.GetTotals();

        // Assert
        Assert.Equal(12345, totals.PromptTokens);
        Assert.Equal(67890, totals.CompletionTokens);
        Assert.Equal(80235, totals.TotalTokens);
    }

    [Fact]
    public void ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        const int iterations = 100;
        const int threadsCount = 10;
        var tasks = new List<Task>();

        // Act - Multiple threads adding usage concurrently
        for (int t = 0; t < threadsCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    _service.AddUsage(10, 5);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        var totals = _service.GetTotals();

        // Assert - Should have exact total (no race conditions)
        var expectedPrompt = threadsCount * iterations * 10;
        var expectedCompletion = threadsCount * iterations * 5;
        Assert.Equal(expectedPrompt, totals.PromptTokens);
        Assert.Equal(expectedCompletion, totals.CompletionTokens);
    }
}
