using System.Text.RegularExpressions;
using AnnasArchive.Core.Helpers;
using Xunit;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// Tests for LibraryUploadEndpoints filename sanitization and validation logic.
/// Tests the internal helper methods that ensure safe file handling.
/// </summary>
public class LibraryUploadEndpointsTests
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2", ".txt", ".rtf", ".lit", ".djvu"
    };

    private const long MaxFileSizeBytes = 500 * 1024 * 1024;

    #region Filename Sanitization Tests

    [Theory]
    [InlineData("book.epub", "book.epub")]
    [InlineData("My Book.pdf", "My Book.pdf")]
    [InlineData("Author - Title.mobi", "Author - Title.mobi")]
    public void SanitizeFileName_ValidFilenames_ReturnsUnchanged(string input, string expected)
    {
        var result = SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/path/to/book.epub", "book.epub")]
    [InlineData("C:\\Users\\test\\book.pdf", "book.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    public void SanitizeFileName_PathTraversal_RemovesPath(string input, string expected)
    {
        var result = SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileName_DoubleDots_ReplacedWithUnderscore()
    {
        var result = SanitizeFileName("book..epub");
        Assert.DoesNotContain("..", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SanitizeFileName_EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        var result = SanitizeFileName(input ?? "");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeFileName_ControlCharacters_Removed()
    {
        // Note the explicit \x0000 / \x001f: C#'s \x escape is variable-length
        // and greedily eats up to four hex digits, so "\x00\x1f" does not mean
        // what it looks like it means. That ambiguity is the likeliest reason
        // this test was originally written as failing and commented out.
        var result = SanitizeFileName("book\x0000\x001f.epub");

        Assert.Equal("book.epub", result);
    }

    [Fact]
    public void SanitizeFileName_DeleteCharacter_Removed()
    {
        // 0x7F sits above the 0x20 floor the old implementation checked, so it
        // used to survive into the filename.
        Assert.Equal("book.epub", SanitizeFileName("book\x007f.epub"));
    }

    [Fact]
    public void SanitizeFileName_VeryLongFilename_Truncated()
    {
        var longName = new string('a', 300) + ".epub";
        var result = SanitizeFileName(longName);
        Assert.True(result.Length <= 255);
        Assert.EndsWith(".epub", result);
    }

    [Theory]
    [InlineData(".hidden", "hidden")]
    [InlineData("...dots", "_.dots")]  // `..` replaced with `_`, leaving `.dots`
    [InlineData("book.epub.", "book.epub")]
    public void SanitizeFileName_LeadingTrailingDots_Trimmed(string input, string expected)
    {
        var result = SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Extension Validation Tests

    [Theory]
    [InlineData(".epub", true)]
    [InlineData(".EPUB", true)]
    [InlineData(".pdf", true)]
    [InlineData(".PDF", true)]
    [InlineData(".mobi", true)]
    [InlineData(".azw3", true)]
    [InlineData(".azw", true)]
    [InlineData(".kfx", true)]
    [InlineData(".pobi", true)]
    [InlineData(".fb2", true)]
    [InlineData(".txt", true)]
    [InlineData(".rtf", true)]
    [InlineData(".lit", true)]
    [InlineData(".djvu", true)]
    public void IsExtensionSupported_SupportedFormats_ReturnsTrue(string extension, bool expected)
    {
        var result = SupportedExtensions.Contains(extension);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".sh")]
    [InlineData(".js")]
    [InlineData(".html")]
    [InlineData(".zip")]
    [InlineData(".rar")]
    [InlineData(".doc")]
    [InlineData(".docx")]
    public void IsExtensionSupported_UnsupportedFormats_ReturnsFalse(string extension)
    {
        var result = SupportedExtensions.Contains(extension);
        Assert.False(result);
    }

    [Fact]
    public void SupportedExtensions_ContainsExpectedCount()
    {
        Assert.Equal(12, SupportedExtensions.Count);
    }

    #endregion

    #region File Size Validation Tests

    [Theory]
    [InlineData(0, true)]
    [InlineData(1024, true)]
    [InlineData(1024 * 1024, true)]
    [InlineData(100 * 1024 * 1024, true)]
    [InlineData(500 * 1024 * 1024, true)]
    public void IsFileSizeValid_UnderLimit_ReturnsTrue(long bytes, bool expected)
    {
        var result = bytes <= MaxFileSizeBytes;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(500 * 1024 * 1024 + 1)]
    [InlineData(600 * 1024 * 1024)]
    [InlineData(1024L * 1024 * 1024)]
    public void IsFileSizeValid_OverLimit_ReturnsFalse(long bytes)
    {
        var result = bytes <= MaxFileSizeBytes;
        Assert.False(result);
    }

    [Fact]
    public void MaxFileSize_Is500MB()
    {
        Assert.Equal(500 * 1024 * 1024, MaxFileSizeBytes);
    }

    #endregion

    #region Filename Extraction Tests

    [Theory]
    [InlineData("book.epub", ".epub")]
    [InlineData("My.Book.With.Dots.pdf", ".pdf")]
    [InlineData("noextension", "")]
    [InlineData(".hiddenfile", "")]
    public void GetFileExtension_VariousFilenames_ReturnsCorrectExtension(string filename, string expected)
    {
        var result = GetFileExtension(filename);
        Assert.Equal(expected, result);
    }

    #endregion

    /// <summary>
    /// Calls the real sanitiser the upload endpoint uses.
    ///
    /// This used to be a hand-copied "mirrors the endpoint logic" duplicate,
    /// which meant these tests asserted against the copy and would have passed
    /// even if the endpoint's own sanitisation were deleted outright. The empty
    /// fallback matches what the endpoint passes, since it treats a blank result
    /// as "reject this upload".
    /// </summary>
    private static string SanitizeFileName(string fileName) =>
        SafeFileName.ForUserInput(fileName, fallback: string.Empty);

    /// <summary>
    /// Helper: Gets file extension from filename.
    /// </summary>
    private static string GetFileExtension(string filename)
    {
        var lastDot = filename.LastIndexOf('.');
        if (lastDot <= 0) return "";
        return filename.Substring(lastDot);
    }
}
