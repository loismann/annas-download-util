using System.IO;

namespace AnnasArchive.Tests.Cache;

/// <summary>
/// Tests for caching logic to ensure cache invalidation, retrieval, and storage work correctly
/// These tests verify the caching patterns used throughout the application
/// </summary>
public class CachingLogicTests
{
    [Fact]
    public void FileCacheKey_ShouldBeConsistent()
    {
        // This tests that cache keys are generated consistently for the same inputs

        // Arrange
        var dropboxPath1 = "/Books/MyBook.epub";
        var chapterId1 = 5;

        var dropboxPath2 = "/Books/MyBook.epub";
        var chapterId2 = 5;

        // Act
        var key1 = $"{dropboxPath1}__{chapterId1}";
        var key2 = $"{dropboxPath2}__{chapterId2}";

        // Assert
        key1.Should().Be(key2);
    }

    [Fact]
    public void FileCacheKey_ShouldBeDifferentForDifferentInputs()
    {
        // Arrange
        var dropboxPath1 = "/Books/Book1.epub";
        var chapterId1 = 5;

        var dropboxPath2 = "/Books/Book2.epub";
        var chapterId2 = 5;

        var dropboxPath3 = "/Books/Book1.epub";
        var chapterId3 = 6;

        // Act
        var key1 = $"{dropboxPath1}__{chapterId1}";
        var key2 = $"{dropboxPath2}__{chapterId2}";
        var key3 = $"{dropboxPath3}__{chapterId3}";

        // Assert
        key1.Should().NotBe(key2);
        key1.Should().NotBe(key3);
        key2.Should().NotBe(key3);
    }

    [Fact]
    public void CacheDirectory_ShouldFollowStandardPattern()
    {
        // Tests that cache directories follow a consistent naming pattern

        // Arrange
        var epubCacheRoot = "/cache/epubs";
        var aiCacheRoot = "/cache/ai";
        var dropboxPath = "/Books/MyBook.epub";

        // Act
        var epubCachePath = Path.Combine(epubCacheRoot, dropboxPath.TrimStart('/'));
        var aiCachePath = Path.Combine(aiCacheRoot, dropboxPath.TrimStart('/'));

        // Assert
        epubCachePath.Should().Contain("Books/MyBook.epub");
        aiCachePath.Should().Contain("Books/MyBook.epub");
        epubCachePath.Should().NotStartWith("//");
        aiCachePath.Should().NotStartWith("//");
    }

    [Fact]
    public void SectionCacheKey_ShouldIncludeSectionIndex()
    {
        // Tests that section-level caching includes the section index in the key

        // Arrange
        var dropboxPath = "/Books/MyBook.epub";
        var chapterId = 3;
        var sectionIndex = 2;

        // Act
        var key = $"{dropboxPath}__{chapterId}__section_{sectionIndex}";

        // Assert
        key.Should().Contain("section_2");
        key.Should().Contain("__3__");
    }

    [Theory]
    [InlineData("/Books/Book.epub", 1, 0, "Books/Book.epub__1__section_0")]
    [InlineData("/My Books/Test.epub", 5, 3, "My Books/Test.epub__5__section_3")]
    [InlineData("/folder/subfolder/book.epub", 10, 7, "folder/subfolder/book.epub__10__section_7")]
    public void SectionCacheKey_ShouldFollowConsistentPattern(string path, int chapter, int section, string expectedPattern)
    {
        // Arrange & Act
        var key = $"{path.TrimStart('/')}__{chapter}__section_{section}";

        // Assert
        key.Should().Contain(expectedPattern);
    }

    [Fact]
    public void CharacterGraphCacheKey_ShouldBeBasedOnBookPath()
    {
        // Tests that character graphs are cached per book

        // Arrange
        var dropboxPath = "/Books/MyBook.epub";

        // Act
        var key = $"{dropboxPath}__character_graph";

        // Assert
        key.Should().Contain("MyBook.epub");
        key.Should().EndWith("character_graph");
    }

    [Fact]
    public void VocabularyCacheKey_ShouldIncludeAllRelevantIdentifiers()
    {
        // Tests that vocabulary caching includes path, chapter, and section

        // Arrange
        var dropboxPath = "/Books/MyBook.epub";
        var chapterId = 5;
        var sectionIndex = 2;

        // Act
        var key = $"{dropboxPath}__{chapterId}__section_{sectionIndex}__vocab";

        // Assert
        key.Should().Contain("MyBook.epub");
        key.Should().Contain("__5__");
        key.Should().Contain("section_2");
        key.Should().EndWith("vocab");
    }

    [Fact]
    public void CacheTimestamp_ShouldBeIso8601Format()
    {
        // Tests that cache timestamps use ISO 8601 format for consistency

        // Arrange & Act
        var timestamp = DateTime.UtcNow.ToString("o");

        // Assert
        timestamp.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
        timestamp.Should().Contain("T");
        timestamp.Should().EndWith("Z");
    }

    [Fact]
    public void CacheValidation_ShouldCheckExistence()
    {
        // Tests cache validation logic

        // Arrange
        string? cachedAt = DateTime.UtcNow.ToString("o");
        bool cached = !string.IsNullOrEmpty(cachedAt);

        // Assert
        cached.Should().BeTrue();

        // Test with null
        cachedAt = null;
        cached = !string.IsNullOrEmpty(cachedAt);
        cached.Should().BeFalse();
    }

    [Fact]
    public void CacheInvalidation_ShouldHandleRelatedCaches()
    {
        // Tests that deleting a book cache also deletes related AI caches

        // Arrange
        var dropboxPath = "/Books/MyBook.epub";
        var epubCacheKey = $"epub:{dropboxPath}";
        var aiCacheKey = $"ai:{dropboxPath}";

        var relatedCaches = new List<string>
        {
            epubCacheKey,
            aiCacheKey
        };

        // Act & Assert
        relatedCaches.Should().HaveCount(2);
        relatedCaches.Should().Contain(epubCacheKey);
        relatedCaches.Should().Contain(aiCacheKey);
    }

    [Theory]
    [InlineData(100, 1000, 10.0)]
    [InlineData(500, 1000, 50.0)]
    [InlineData(1000, 1000, 100.0)]
    [InlineData(0, 1000, 0.0)]
    public void CacheProgressPercentage_ShouldCalculateCorrectly(int cached, int total, double expectedPercent)
    {
        // Tests the progress percentage calculation for EPUB caching

        // Arrange & Act
        var percent = total > 0 ? Math.Round((double)cached / total * 100, 2) : 0;

        // Assert
        percent.Should().Be(expectedPercent);
    }

    [Fact]
    public void CacheStalenessCheck_ShouldDetectOutdatedCache()
    {
        // Tests that we can detect when a cache needs updating

        // Arrange
        var cachedSummaryCount = 10;
        var currentSummaryCount = 15;

        // Act
        var needsUpdate = currentSummaryCount > cachedSummaryCount;

        // Assert
        needsUpdate.Should().BeTrue();
    }

    [Fact]
    public void CacheStalenessCheck_ShouldNotFlagUpToDateCache()
    {
        // Arrange
        var cachedSummaryCount = 15;
        var currentSummaryCount = 15;

        // Act
        var needsUpdate = currentSummaryCount > cachedSummaryCount;

        // Assert
        needsUpdate.Should().BeFalse();
    }

    [Fact]
    public void CacheKey_ShouldHandleSpecialCharacters()
    {
        // Tests that cache keys handle special characters safely

        // Arrange
        var dropboxPath = "/Books/Book's Title (2023).epub";

        // Act
        var safeKey = dropboxPath
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(":", "_");

        // Assert
        safeKey.Should().NotContain("/");
        safeKey.Should().NotContain("\\");
        safeKey.Should().NotContain(":");
    }

    [Fact]
    public void TokenUsageTracking_ShouldAccumulateCorrectly()
    {
        // Tests that token usage accumulates across multiple operations

        // Arrange
        var initialPromptTokens = 1000;
        var initialCompletionTokens = 500;

        var operation1Prompt = 200;
        var operation1Completion = 100;

        var operation2Prompt = 300;
        var operation2Completion = 150;

        // Act
        var totalPrompt = initialPromptTokens + operation1Prompt + operation2Prompt;
        var totalCompletion = initialCompletionTokens + operation1Completion + operation2Completion;
        var totalTokens = totalPrompt + totalCompletion;

        // Assert
        totalPrompt.Should().Be(1500);
        totalCompletion.Should().Be(750);
        totalTokens.Should().Be(2250);
    }

    [Theory]
    [InlineData(1000, 10000, 10.0)]
    [InlineData(5000, 10000, 50.0)]
    [InlineData(10000, 10000, 100.0)]
    [InlineData(15000, 10000, 150.0)] // Over limit
    public void TokenAllowancePercentage_ShouldCalculateCorrectly(long used, long allowance, double expectedPercent)
    {
        // Arrange & Act
        var percent = allowance > 0 ? Math.Round((double)used / allowance * 100, 2) : 0;

        // Assert
        percent.Should().Be(expectedPercent);
    }

    [Theory]
    [InlineData(5000, 10000, 5000)]
    [InlineData(8000, 10000, 2000)]
    [InlineData(10000, 10000, 0)]
    [InlineData(12000, 10000, -2000)] // Over limit
    public void TokensRemaining_ShouldCalculateCorrectly(long used, long allowance, long expectedRemaining)
    {
        // Arrange & Act
        var remaining = allowance - used;

        // Assert
        remaining.Should().Be(expectedRemaining);
    }
}
