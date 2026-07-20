using AnnasArchive.Core.Services;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class TextProcessingServiceTests
{
    private readonly TextProcessingService _service;

    public TextProcessingServiceTests()
    {
        _service = new TextProcessingService();
    }

    #region ExtractWordOffset Tests

    [Theory]
    [InlineData("summary-0-100", 100)]
    [InlineData("summary-1-500", 500)]
    [InlineData("summary-10-0", 0)]
    [InlineData("summary-5-999999", 999999)]
    public void ExtractWordOffset_ValidFormat_ShouldExtractOffset(string filename, int expected)
    {
        // Act
        var result = _service.ExtractWordOffset(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("summary-0")] // Missing offset
    [InlineData("summary")] // Missing chapter and offset
    [InlineData("summary-0-abc")] // Non-numeric offset
    [InlineData("invalid-format")] // Completely wrong format
    [InlineData("")] // Empty
    public void ExtractWordOffset_InvalidFormat_ShouldReturnMaxValue(string filename)
    {
        // Act
        var result = _service.ExtractWordOffset(filename);

        // Assert
        Assert.Equal(int.MaxValue, result);
    }

    [Fact]
    public void ExtractWordOffset_ExtraHyphens_ShouldStillParse()
    {
        // Arrange - filename with extra hyphens after the offset
        var filename = "summary-0-250-extra-data";

        // Act
        var result = _service.ExtractWordOffset(filename);

        // Assert
        Assert.Equal(250, result);
    }

    #endregion

    #region BuildAnalysisPrompt Tests

    [Fact]
    public void BuildAnalysisPrompt_WithPreviousAnalyses_ShouldIncludeThem()
    {
        // Arrange
        var context = "Book: Test Book";
        var previous = "Previous analysis content";
        var current = "Current passage";

        // Act
        var result = _service.BuildAnalysisPrompt(context, previous, current);

        // Assert
        Assert.Contains(context, result);
        Assert.Contains("Previous analyses from this reading session:", result);
        Assert.Contains(previous, result);
        Assert.Contains("Analyze this passage:", result);
        Assert.Contains(current, result);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithoutPreviousAnalyses_ShouldNotIncludeThem()
    {
        // Arrange
        var context = "Book: Test Book";
        var current = "Current passage";

        // Act
        var result = _service.BuildAnalysisPrompt(context, null, current);

        // Assert
        Assert.Contains(context, result);
        Assert.DoesNotContain("Previous analyses", result);
        Assert.Contains("Analyze this passage:", result);
        Assert.Contains(current, result);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithEmptyPreviousAnalyses_ShouldNotIncludeThem()
    {
        // Arrange
        var context = "Book: Test Book";
        var current = "Current passage";

        // Act
        var result = _service.BuildAnalysisPrompt(context, "   ", current);

        // Assert
        Assert.DoesNotContain("Previous analyses", result);
    }

    [Fact]
    public void BuildAnalysisPrompt_ShouldFormatCorrectly()
    {
        // Arrange
        var context = "Context line";
        var current = "Text to analyze";

        // Act
        var result = _service.BuildAnalysisPrompt(context, null, current);

        // Assert
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Context line", lines[0]);
        Assert.Contains("Analyze this passage:", result);
        Assert.Contains("Text to analyze", result);
    }

    #endregion

    #region SplitIntoChunks Tests

    [Fact]
    public void SplitIntoChunks_EmptyText_ShouldReturnEmptyList()
    {
        // Act
        var result = _service.SplitIntoChunks("", 100);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_NullText_ShouldReturnEmptyList()
    {
        // Act
        var result = _service.SplitIntoChunks(null!, 100);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_WhitespaceText_ShouldReturnEmptyList()
    {
        // Act
        var result = _service.SplitIntoChunks("   ", 100);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_ZeroMaxWords_ShouldReturnEmptyList()
    {
        // Act
        var result = _service.SplitIntoChunks("word1 word2 word3", 0);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_NegativeMaxWords_ShouldReturnEmptyList()
    {
        // Act
        var result = _service.SplitIntoChunks("word1 word2 word3", -10);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_TextShorterThanMax_ShouldReturnSingleChunk()
    {
        // Arrange
        var text = "one two three";

        // Act
        var result = _service.SplitIntoChunks(text, 10);

        // Assert
        Assert.Single(result);
        Assert.Equal("one two three", result[0]);
    }

    [Fact]
    public void SplitIntoChunks_TextExactlyMaxWords_ShouldReturnSingleChunk()
    {
        // Arrange
        var text = "one two three";

        // Act
        var result = _service.SplitIntoChunks(text, 3);

        // Assert
        Assert.Single(result);
        Assert.Equal("one two three", result[0]);
    }

    [Fact]
    public void SplitIntoChunks_TextLongerThanMax_ShouldReturnMultipleChunks()
    {
        // Arrange
        var text = "one two three four five six seven eight nine ten";

        // Act
        var result = _service.SplitIntoChunks(text, 3);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("one two three", result[0]);
        Assert.Equal("four five six", result[1]);
        Assert.Equal("seven eight nine", result[2]);
        Assert.Equal("ten", result[3]);
    }

    [Fact]
    public void SplitIntoChunks_MultipleSpaces_ShouldHandleCorrectly()
    {
        // Arrange
        var text = "one   two    three     four";

        // Act
        var result = _service.SplitIntoChunks(text, 2);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("one two", result[0]);
        Assert.Equal("three four", result[1]);
    }

    [Fact]
    public void SplitIntoChunks_NewlinesAndTabs_ShouldTreatAsWhitespace()
    {
        // Arrange
        var text = "one\ntwo\tthree\r\nfour";

        // Act
        var result = _service.SplitIntoChunks(text, 2);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("one two", result[0]);
        Assert.Equal("three four", result[1]);
    }

    [Fact]
    public void SplitIntoChunks_LargeText_ShouldSplitCorrectly()
    {
        // Arrange
        var words = Enumerable.Range(1, 1000).Select(i => $"word{i}");
        var text = string.Join(" ", words);

        // Act
        var result = _service.SplitIntoChunks(text, 100);

        // Assert
        Assert.Equal(10, result.Count);
        Assert.Contains("word1", result[0]);
        Assert.Contains("word100", result[0]);
        Assert.Contains("word101", result[1]);
        Assert.Contains("word1000", result[9]);
    }

    #endregion
}
