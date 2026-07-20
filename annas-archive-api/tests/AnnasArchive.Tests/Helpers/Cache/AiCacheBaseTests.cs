using AnnasArchive.API.Helpers.Cache;
using Xunit;

namespace AnnasArchive.Tests.Helpers.Cache;

public class AiCacheBaseTests
{
    #region SanitizeKey / SanitizeForFilename Tests

    [Fact]
    public void SanitizeKey_ReplacesInvalidChars()
    {
        // Path separators should be replaced
        Assert.Equal("path_to_file", AiCacheBase.SanitizeKey("path/to/file"));
        Assert.Equal("path_to_file", AiCacheBase.SanitizeKey("path\\to\\file"));
    }

    [Fact]
    public void SanitizeKey_ReplacesColons()
    {
        Assert.Equal("file_name", AiCacheBase.SanitizeKey("file:name"));
    }

    [Fact]
    public void SanitizeKey_ReplacesQuestionMarks()
    {
        Assert.Equal("what_is_this", AiCacheBase.SanitizeKey("what?is?this"));
    }

    [Fact]
    public void SanitizeKey_ReplacesAsterisks()
    {
        Assert.Equal("wild_card", AiCacheBase.SanitizeKey("wild*card"));
    }

    [Fact]
    public void SanitizeKey_ReplacesPipeCharacter()
    {
        Assert.Equal("pipe_test", AiCacheBase.SanitizeKey("pipe|test"));
    }

    [Fact]
    public void SanitizeKey_ReplacesAngleBrackets()
    {
        Assert.Equal("_tag_", AiCacheBase.SanitizeKey("<tag>"));
    }

    [Fact]
    public void SanitizeKey_TruncatesLongStrings()
    {
        var longInput = new string('a', 300);
        var result = AiCacheBase.SanitizeKey(longInput);
        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void SanitizeKey_PreservesValidChars()
    {
        Assert.Equal("valid-file_name.txt", AiCacheBase.SanitizeKey("valid-file_name.txt"));
        Assert.Equal("file123", AiCacheBase.SanitizeKey("file123"));
    }

    [Fact]
    public void SanitizeKey_HandlesEmptyString()
    {
        Assert.Equal("", AiCacheBase.SanitizeKey(""));
    }

    [Fact]
    public void SanitizeKey_HandlesMixedInvalidChars()
    {
        Assert.Equal("a_b_c_d_e", AiCacheBase.SanitizeKey("a/b\\c:d?e"));
    }

    #endregion

    #region HasAnySummaries Tests

    [Fact]
    public void HasAnySummaries_ReturnsFalse_WhenKeyIsNull()
    {
        var existingKeys = new HashSet<string> { "key1", "key2" };
        Assert.False(AiCacheBase.HasAnySummaries(null!, existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsFalse_WhenKeyIsEmpty()
    {
        var existingKeys = new HashSet<string> { "key1", "key2" };
        Assert.False(AiCacheBase.HasAnySummaries("", existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsFalse_WhenKeyIsWhitespace()
    {
        var existingKeys = new HashSet<string> { "key1", "key2" };
        Assert.False(AiCacheBase.HasAnySummaries("   ", existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsTrue_WhenKeyExists()
    {
        var existingKeys = new HashSet<string> { "key1", "key2" };
        Assert.True(AiCacheBase.HasAnySummaries("key1", existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsTrue_WhenSanitizedKeyExists()
    {
        // If we have "path_to_book" in existing keys, and query with "path/to/book"
        var existingKeys = new HashSet<string> { "path_to_book" };
        Assert.True(AiCacheBase.HasAnySummaries("path/to/book", existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsFalse_WhenKeyNotFound()
    {
        var existingKeys = new HashSet<string> { "key1", "key2" };
        Assert.False(AiCacheBase.HasAnySummaries("key3", existingKeys));
    }

    [Fact]
    public void HasAnySummaries_ReturnsFalse_WhenSetIsEmpty()
    {
        var existingKeys = new HashSet<string>();
        Assert.False(AiCacheBase.HasAnySummaries("anykey", existingKeys));
    }

    #endregion
}
