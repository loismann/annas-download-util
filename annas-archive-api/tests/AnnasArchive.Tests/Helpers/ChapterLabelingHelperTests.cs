using AnnasArchive.API.Helpers;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class ChapterLabelingHelperTests
{
    #region SanitizeChapterLabelJson Tests

    [Fact]
    public void SanitizeChapterLabelJson_ReturnsNull_WhenEmpty()
    {
        Assert.Null(ChapterLabelingHelper.SanitizeChapterLabelJson(""));
        Assert.Null(ChapterLabelingHelper.SanitizeChapterLabelJson("   "));
        Assert.Null(ChapterLabelingHelper.SanitizeChapterLabelJson(null!));
    }

    [Fact]
    public void SanitizeChapterLabelJson_ReturnsJsonArray_WhenAlreadyValid()
    {
        var input = "[{\"id\": 1, \"displayLabel\": \"Chapter 1\"}]";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_ExtractsJsonFromMarkdownCodeBlock()
    {
        var input = "```json\n[{\"id\": 1, \"displayLabel\": \"Chapter 1\"}]\n```";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("[{\"id\": 1, \"displayLabel\": \"Chapter 1\"}]", result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_ExtractsJsonFromUnlabeledCodeBlock()
    {
        var input = "```\n[{\"id\": 1}]\n```";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("[{\"id\": 1}]", result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_ExtractsArrayFromSurroundingText()
    {
        var input = "Here is the result:\n[{\"id\": 1}]\nDone!";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("[{\"id\": 1}]", result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_HandlesNestedBrackets()
    {
        var input = "[{\"id\": 1, \"data\": [1, 2, 3]}]";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("[{\"id\": 1, \"data\": [1, 2, 3]}]", result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_TrimsWhitespace()
    {
        var input = "   [{\"id\": 1}]   ";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("[{\"id\": 1}]", result);
    }

    [Fact]
    public void SanitizeChapterLabelJson_ReturnsOriginal_WhenNoBrackets()
    {
        var input = "invalid response";
        var result = ChapterLabelingHelper.SanitizeChapterLabelJson(input);
        Assert.Equal("invalid response", result);
    }

    #endregion
}
