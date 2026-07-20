using AnnasArchive.Core.Services;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class ValidationServiceTests
{
    private readonly ValidationService _service;

    public ValidationServiceTests()
    {
        _service = new ValidationService();
    }

    #region IsValidMd5 Tests

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", true)] // Valid MD5
    [InlineData("D41D8CD98F00B204E9800998ECF8427E", true)] // Valid MD5 uppercase
    [InlineData("00000000000000000000000000000000", true)] // All zeros
    [InlineData("ffffffffffffffffffffffffffffffff", true)] // All f's
    [InlineData("d41d8cd98f00b204e9800998ecf8427", false)] // Too short (31 chars)
    [InlineData("d41d8cd98f00b204e9800998ecf8427e0", false)] // Too long (33 chars)
    [InlineData("d41d8cd98f00b204e9800998ecf8427g", false)] // Invalid char 'g'
    [InlineData("d41d8cd98f00b204e9800998ecf8427Z", false)] // Invalid char 'Z'
    [InlineData("", false)] // Empty
    [InlineData(null, false)] // Null
    [InlineData("   ", false)] // Whitespace
    public void IsValidMd5_ShouldValidateCorrectly(string? md5, bool expected)
    {
        // Act
        var result = _service.IsValidMd5(md5!);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region IsValidDropboxPath Tests

    [Theory]
    [InlineData("/books/test.epub", true)]
    [InlineData("/test.epub", true)]
    [InlineData("/folder/subfolder/book.epub", true)]
    [InlineData("/book.EPUB", true)] // Case insensitive
    [InlineData("/books/Test Book.epub", true)] // Spaces allowed
    [InlineData("books/test.epub", false)] // Missing leading /
    [InlineData("/books/test.txt", false)] // Not .epub
    [InlineData("/books/../test.epub", false)] // Path traversal
    [InlineData("/books/~/test.epub", false)] // Tilde
    [InlineData("", false)] // Empty
    [InlineData(null, false)] // Null
    [InlineData("   ", false)] // Whitespace
    public void IsValidDropboxPath_ShouldValidateCorrectly(string? path, bool expected)
    {
        // Act
        var result = _service.IsValidDropboxPath(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValidDropboxPath_TooLong_ShouldReturnFalse()
    {
        // Arrange - Create a path longer than 500 chars
        var longPath = "/" + new string('a', 495) + ".epub"; // 501 chars total

        // Act
        var result = _service.IsValidDropboxPath(longPath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidDropboxPath_ExactlyMaxLength_ShouldReturnTrue()
    {
        // Arrange - Create a path exactly 500 chars
        var maxPath = "/" + new string('a', 494) + ".epub"; // 500 chars total

        // Act
        var result = _service.IsValidDropboxPath(maxPath);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region IsValidChapterId Tests

    [Theory]
    [InlineData(0, true)] // Minimum valid
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(9999, true)] // Maximum valid
    [InlineData(-1, false)] // Negative
    [InlineData(-100, false)]
    [InlineData(10000, false)] // At limit
    [InlineData(10001, false)] // Over limit
    public void IsValidChapterId_ShouldValidateCorrectly(int chapterId, bool expected)
    {
        // Act
        var result = _service.IsValidChapterId(chapterId);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region IsValidTextLength Tests

    [Fact]
    public void IsValidTextLength_EmptyString_ShouldReturnTrue()
    {
        // Act
        var result = _service.IsValidTextLength("");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidTextLength_Null_ShouldReturnTrue()
    {
        // Act
        var result = _service.IsValidTextLength(null);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidTextLength_UnderLimit_ShouldReturnTrue()
    {
        // Arrange
        var text = new string('a', 999_999);

        // Act
        var result = _service.IsValidTextLength(text);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidTextLength_AtLimit_ShouldReturnTrue()
    {
        // Arrange
        var text = new string('a', 1_000_000);

        // Act
        var result = _service.IsValidTextLength(text);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidTextLength_OverLimit_ShouldReturnFalse()
    {
        // Arrange
        var text = new string('a', 1_000_001);

        // Act
        var result = _service.IsValidTextLength(text);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidTextLength_CustomLimit_ShouldUseCustom()
    {
        // Arrange
        var text = new string('a', 101);

        // Act
        var result = _service.IsValidTextLength(text, maxLength: 100);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region IsValidSearchQuery Tests

    [Theory]
    [InlineData("a", true)] // Min length
    [InlineData("test", true)]
    [InlineData("  test  ", true)] // Trimmed
    [InlineData("", false)] // Empty
    [InlineData(null, false)] // Null
    [InlineData("   ", false)] // Whitespace only
    public void IsValidSearchQuery_DefaultParams_ShouldValidateCorrectly(string? query, bool expected)
    {
        // Act
        var result = _service.IsValidSearchQuery(query);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValidSearchQuery_MaxLength_ShouldEnforce()
    {
        // Arrange
        var query = new string('a', 501);

        // Act
        var result = _service.IsValidSearchQuery(query);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidSearchQuery_CustomMinLength_ShouldEnforce()
    {
        // Arrange
        var query = "ab"; // 2 chars

        // Act
        var result = _service.IsValidSearchQuery(query, minLength: 3);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidSearchQuery_CustomMaxLength_ShouldEnforce()
    {
        // Arrange
        var query = "test"; // 4 chars

        // Act
        var result = _service.IsValidSearchQuery(query, maxLength: 3);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region IsValidTitle Tests

    [Theory]
    [InlineData("", true)] // Empty is valid
    [InlineData(null, true)] // Null is valid
    [InlineData("Short Title", true)]
    [InlineData("A", true)]
    public void IsValidTitle_ValidTitles_ShouldReturnTrue(string? title, bool expected)
    {
        // Act
        var result = _service.IsValidTitle(title);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValidTitle_MaxLength_ShouldEnforce()
    {
        // Arrange
        var title = new string('a', 501);

        // Act
        var result = _service.IsValidTitle(title);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidTitle_CustomMaxLength_ShouldEnforce()
    {
        // Arrange
        var title = new string('a', 11);

        // Act
        var result = _service.IsValidTitle(title, maxLength: 10);

        // Assert
        Assert.False(result);
    }

    #endregion
}
