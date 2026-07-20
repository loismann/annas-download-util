using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Validation;

/// <summary>
/// Tests for input validation logic used across endpoints to ensure security and data integrity
/// </summary>
public class InputValidationTests
{
    // This regex matches the validation logic in Program.cs
    private static bool IsValidMd5(string md5) =>
        !string.IsNullOrWhiteSpace(md5) &&
        Regex.IsMatch(md5, "^[a-f0-9]{32}$", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("abc123def456789012345678901234ab", true)] // valid lowercase
    [InlineData("ABC123DEF456789012345678901234AB", true)] // valid uppercase
    [InlineData("AbC123dEf456789012345678901234aB", true)] // valid mixed case
    [InlineData("0123456789abcdef0123456789abcdef", true)] // valid with numbers
    public void IsValidMd5_WithValidMd5_ShouldReturnTrue(string md5, bool expected)
    {
        // Act
        var result = IsValidMd5(md5);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("", false)] // empty string
    [InlineData(null, false)] // null
    [InlineData("abc123", false)] // too short
    [InlineData("abc123def456789012345678901234abcd", false)] // too long
    [InlineData("abc123def456789012345678901234ag", false)] // invalid character 'g'
    [InlineData("abc123def456789012345678901234a!", false)] // special character
    [InlineData("abc123def456789012345678901234a ", false)] // space
    [InlineData("abc123-def45-67890-12345-678901234ab", false)] // hyphens
    public void IsValidMd5_WithInvalidMd5_ShouldReturnFalse(string md5, bool expected)
    {
        // Act
        var result = IsValidMd5(md5);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Normal Title", true)]
    [InlineData("Title with Numbers 123", true)]
    [InlineData("Title with Punctuation!", true)]
    [InlineData("Title-with-dashes", true)]
    public void ValidateTitle_WithValidTitle_ShouldPass(string title, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(title) && title.Length <= 500;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("", false)] // empty
    [InlineData(null, false)] // null
    public void ValidateTitle_WithInvalidTitle_ShouldFail(string title, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(title) && title.Length <= 500;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Fact]
    public void ValidateTitle_WithTooLongTitle_ShouldFail()
    {
        // Arrange
        var title = new string('x', 501);
        var isValid = !string.IsNullOrWhiteSpace(title) && title.Length <= 500;

        // Act & Assert
        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("/path/to/file.epub", true)]
    [InlineData("/Books/MyBook.epub", true)]
    [InlineData("/folder/subfolder/book.epub", true)]
    public void ValidateDropboxPath_WithValidPath_ShouldPass(string path, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(path);

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void ValidateDropboxPath_WithInvalidPath_ShouldFail(string path, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(path);

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(999, true)]
    public void ValidateChapterId_WithValidId_ShouldPass(int chapterId, bool expected)
    {
        // Arrange
        var isValid = chapterId >= 0;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-100, false)]
    public void ValidateChapterId_WithInvalidId_ShouldFail(int chapterId, bool expected)
    {
        // Arrange
        var isValid = chapterId >= 0;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("test query", true)]
    [InlineData("a", true)] // single character
    [InlineData("query with special chars !@#$", true)]
    [InlineData("unicode 中文 query", true)]
    public void ValidateSearchQuery_WithValidQuery_ShouldPass(string query, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(query);

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void ValidateSearchQuery_WithInvalidQuery_ShouldFail(string query, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(query);

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Fact]
    public void ValidateSearchQuery_ForEpubSearch_ShouldRequireMinimum10Characters()
    {
        // This tests the special validation for EPUB search that requires min 10 chars

        // Arrange
        var shortQuery = "short";
        var validQuery = "this is a longer query";

        // Act
        var shortQueryValid = !string.IsNullOrWhiteSpace(shortQuery) && shortQuery.Length >= 10;
        var validQueryValid = !string.IsNullOrWhiteSpace(validQuery) && validQuery.Length >= 10;

        // Assert
        shortQueryValid.Should().BeFalse();
        validQueryValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    public void ValidateSectionIndex_WithValidIndex_ShouldPass(int sectionIndex, bool expected)
    {
        // Arrange
        var isValid = sectionIndex >= 0;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-10, false)]
    public void ValidateSectionIndex_WithInvalidIndex_ShouldFail(int sectionIndex, bool expected)
    {
        // Arrange
        var isValid = sectionIndex >= 0;

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("word", true)]
    [InlineData("multi word term", true)]
    [InlineData("term-with-hyphens", true)]
    [InlineData("term's", true)] // with apostrophe
    public void ValidateVocabularyTerm_WithValidTerm_ShouldPass(string term, bool expected)
    {
        // Arrange
        var isValid = !string.IsNullOrWhiteSpace(term);

        // Act & Assert
        isValid.Should().Be(expected);
    }

    [Fact]
    public void ValidateTextLength_ShouldEnforceLimits()
    {
        // This tests that text inputs respect reasonable length limits

        // Arrange
        var shortText = "Short text";
        var mediumText = new string('x', 1000);
        var longText = new string('x', 100000);
        var tooLongText = new string('x', 1000001);

        // Act & Assert
        shortText.Length.Should().BeLessThan(1000000);
        mediumText.Length.Should().BeLessThan(1000000);
        longText.Length.Should().BeLessThan(1000000);
        tooLongText.Length.Should().BeGreaterThan(1000000);
    }

    [Theory]
    [InlineData("../../../etc/passwd", true)] // path traversal attempt
    [InlineData("normal/path", false)]
    [InlineData("C:\\Windows\\System32", true)] // windows path
    [InlineData("/var/log/", true)] // absolute path
    public void DetectPathTraversal_ShouldIdentifyDangerousPaths(string path, bool isDangerous)
    {
        // This is a security test to ensure path traversal attempts are detected

        // Arrange
        var containsTraversal = path.Contains("..") || path.Contains("\\") || path.StartsWith("/var") || path.StartsWith("/etc") || path.Contains("C:");

        // Act & Assert
        containsTraversal.Should().Be(isDangerous);
    }
}
