using AnnasArchive.API.Helpers.Cache;
using Xunit;

namespace AnnasArchive.Tests.Helpers.Cache;

public class VocabularyCacheTests
{
    #region NormalizeTerm Tests

    [Fact]
    public void NormalizeTerm_ReturnsEmpty_WhenNull()
    {
        Assert.Equal(string.Empty, VocabularyCache.NormalizeTerm(null!));
    }

    [Fact]
    public void NormalizeTerm_ReturnsEmpty_WhenWhitespace()
    {
        Assert.Equal(string.Empty, VocabularyCache.NormalizeTerm("   "));
    }

    [Fact]
    public void NormalizeTerm_ConvertsToLowercase()
    {
        Assert.Equal("hello", VocabularyCache.NormalizeTerm("HELLO"));
        Assert.Equal("world", VocabularyCache.NormalizeTerm("World"));
    }

    [Fact]
    public void NormalizeTerm_NormalizesCurlyQuotes()
    {
        // Left single quote U+2018
        Assert.Equal("don't", VocabularyCache.NormalizeTerm("don\u2018t"));
        // Right single quote U+2019
        Assert.Equal("don't", VocabularyCache.NormalizeTerm("don\u2019t"));
    }

    [Fact]
    public void NormalizeTerm_ReplacesHyphensWithSpaces()
    {
        Assert.Equal("root book", VocabularyCache.NormalizeTerm("root-book"));
        Assert.Equal("self aware", VocabularyCache.NormalizeTerm("self-aware"));
    }

    [Fact]
    public void NormalizeTerm_RemovesPunctuation()
    {
        Assert.Equal("hello world", VocabularyCache.NormalizeTerm("hello! world?"));
        Assert.Equal("test", VocabularyCache.NormalizeTerm("test..."));
        Assert.Equal("a b c", VocabularyCache.NormalizeTerm("a, b; c:"));
    }

    [Fact]
    public void NormalizeTerm_PreservesApostrophes()
    {
        Assert.Equal("it'", VocabularyCache.NormalizeTerm("it's")); // 's gets stripped
        Assert.Equal("don't", VocabularyCache.NormalizeTerm("don't"));
    }

    [Fact]
    public void NormalizeTerm_CollapsesMultipleSpaces()
    {
        Assert.Equal("hello world", VocabularyCache.NormalizeTerm("hello    world"));
        Assert.Equal("a b c", VocabularyCache.NormalizeTerm("a   b   c"));
    }

    [Fact]
    public void NormalizeTerm_StripsPossessives()
    {
        Assert.Equal("john", VocabularyCache.NormalizeTerm("john's"));
        Assert.Equal("cat", VocabularyCache.NormalizeTerm("cat's"));
    }

    [Fact]
    public void NormalizeTerm_StripsSimplePlurals()
    {
        Assert.Equal("book", VocabularyCache.NormalizeTerm("books"));
        Assert.Equal("cat", VocabularyCache.NormalizeTerm("cats"));
    }

    [Fact]
    public void NormalizeTerm_DoesNotStripShortWords()
    {
        // Words 3 chars or less should not have 's' stripped
        Assert.Equal("is", VocabularyCache.NormalizeTerm("is"));
        Assert.Equal("as", VocabularyCache.NormalizeTerm("as"));
        Assert.Equal("bus", VocabularyCache.NormalizeTerm("bus"));
    }

    [Fact]
    public void NormalizeTerm_HandlesComplexInput()
    {
        // Complex case combining multiple transformations
        Assert.Equal("john doe' book", VocabularyCache.NormalizeTerm("John Doe's Books!"));
    }

    [Fact]
    public void NormalizeTerm_TrimsWhitespace()
    {
        Assert.Equal("hello", VocabularyCache.NormalizeTerm("  hello  "));
    }

    [Fact]
    public void NormalizeTerm_HandlesNumbers()
    {
        Assert.Equal("chapter 1", VocabularyCache.NormalizeTerm("Chapter 1"));
        Assert.Equal("2024", VocabularyCache.NormalizeTerm("2024"));
    }

    #endregion
}
