using AnnasArchive.API.Services.Library;
using Xunit;

namespace AnnasArchive.Tests.Services.Library;

public class MetadataExtractionServiceTests
{
    private readonly MetadataExtractionService _service = new();

    #region ParseTitleAuthorFromFileName Tests

    [Fact]
    public void ParseTitleAuthorFromFileName_WithNullPath_ReturnsNulls()
    {
        var result = _service.ParseTitleAuthorFromFileName(null!);
        Assert.Null(result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_WithEmptyPath_ReturnsNulls()
    {
        var result = _service.ParseTitleAuthorFromFileName("");
        Assert.Null(result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_SimpleTitle_ReturnsTitleOnly()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_AuthorDashTitle_ParsesBoth()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/John Smith - The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Single(result.Authors);
        Assert.Equal("John Smith", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_AuthorWithComma_RecognizedAsAuthor()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/Smith, John - The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("Smith, John", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_EnDash_ParsesCorrectly()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/John Smith – The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("John Smith", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_EmDash_ParsesCorrectly()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/John Smith — The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("John Smith", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_RemovesSampleSuffix()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book_sample.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_RemovesPreviewSuffix()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book_preview.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_RemovesTmpSuffix()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book.tmp_abc123.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_RemovesLongHashSuffix()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book_ABCD1234EFGH.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_RemovesAmazonASIN()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book B0ABCDEFGH12.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_ReplacesUnderscoresWithSpaces()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The_Great_Book.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_CollapsesMultipleSpaces()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The   Great    Book.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_ShortLeftPart_NotRecognizedAsAuthor()
    {
        // "AB" is too short (< 3 chars) to be considered an author
        var result = _service.ParseTitleAuthorFromFileName("/path/to/AB - The Great Book.epub");
        // Should return the whole thing as title since left part is too short
        Assert.Equal("AB - The Great Book", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_ShortRightPart_NotParsedAsTitleAuthor()
    {
        // "AB" is too short (< 3 chars) to be considered a title
        var result = _service.ParseTitleAuthorFromFileName("/path/to/John Smith - AB.epub");
        Assert.Equal("John Smith - AB", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_SingleWordAuthor_NotRecognizedAsAuthor()
    {
        // Single word without comma is not recognized as author name
        var result = _service.ParseTitleAuthorFromFileName("/path/to/Word - The Great Book.epub");
        Assert.Equal("Word - The Great Book", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_TwoWordAuthor_RecognizedAsAuthor()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/First Last - The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("First Last", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_DifferentExtensions_AllWork()
    {
        var extensions = new[] { ".epub", ".pdf", ".mobi", ".azw3", ".txt" };

        foreach (var ext in extensions)
        {
            var result = _service.ParseTitleAuthorFromFileName($"/path/to/The Great Book{ext}");
            Assert.Equal("The Great Book", result.Title);
        }
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_ComplexFilename_HandlesMultipleCleaning()
    {
        // Filename with multiple patterns that need cleaning
        var result = _service.ParseTitleAuthorFromFileName(
            "/path/to/John_Smith - The_Great_Book_sample.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("John Smith", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_DashAtStart_NotParsed()
    {
        // Dash at very start shouldn't be treated as separator
        var result = _service.ParseTitleAuthorFromFileName("/path/to/- The Great Book.epub");
        // idx <= 0 check in code
        Assert.NotNull(result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_DashAtEnd_NotParsed()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/The Great Book -.epub");
        // idx >= cleaned.Length - sep.Length check
        Assert.NotNull(result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsBracketedContent()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/[homes_pferrer] The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsMultipleBrackets()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/[homes_pferrer] [retail] The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsBracketsAndParsesAuthor()
    {
        // Note: Code expects "Author - Title" format, not "Title - Author"
        var result = _service.ParseTitleAuthorFromFileName(
            "/path/to/[homes_pferrer] [Wings of Fire 10] Tui T Sutherland - Darkness of Dragons [retail].epub");
        Assert.Equal("Darkness of Dragons", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("Tui T Sutherland", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsLeadingNumber()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/1 - The Butlerian Jihad.epub");
        Assert.Equal("The Butlerian Jihad", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsLeadingNumberWithZeros()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/002 - The Butlerian Jihad.epub");
        Assert.Equal("The Butlerian Jihad", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsLeadingNumberWithDot()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/10. The Butlerian Jihad.epub");
        Assert.Equal("The Butlerian Jihad", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsParenthesizedPrefix()
    {
        var result = _service.ParseTitleAuthorFromFileName("/path/to/(epub) The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_ComplexSynologyFilename()
    {
        // Real-world example from user's Synology drive
        var result = _service.ParseTitleAuthorFromFileName(
            "/path/to/[homes_pferrer] 1 - The Butlerian Jihad.epub");
        Assert.Equal("The Butlerian Jihad", result.Title);
        Assert.Null(result.Authors);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_BracketsWithAuthorDash()
    {
        var result = _service.ParseTitleAuthorFromFileName(
            "/path/to/[retail] John Smith - The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.NotNull(result.Authors);
        Assert.Equal("John Smith", result.Authors[0]);
    }

    [Fact]
    public void ParseTitleAuthorFromFileName_StripsLeadingDashAfterBrackets()
    {
        // When brackets are removed, a leading " - " separator may remain
        var result = _service.ParseTitleAuthorFromFileName(
            "/path/to/[tag] - The Great Book.epub");
        Assert.Equal("The Great Book", result.Title);
        Assert.Null(result.Authors);
    }

    #endregion

    #region ExtractEpubMetadataAsync Integration Test Notes

    // Note: ExtractEpubMetadataAsync requires actual EPUB files to test properly.
    // These should be integration tests with test EPUB fixtures.
    // Test scenarios to cover in integration tests:
    // - Valid EPUB with complete metadata (title, authors, date, page count, cover)
    // - EPUB with missing metadata elements
    // - EPUB with cover in manifest by ID
    // - EPUB with cover by properties="cover-image"
    // - EPUB with no declared cover (falls back to largest image)
    // - Corrupted/invalid EPUB file
    // - EPUB without container.xml
    // - EPUB without OPF file

    #endregion

    #region ExtractSdrCoverAsync Integration Test Notes

    // Note: ExtractSdrCoverAsync requires filesystem setup with .sdr folders.
    // Test scenarios for integration tests:
    // - .sdr folder with cover.jpg
    // - .sdr folder with multiple images (should pick "cover" named or largest)
    // - .sdr folder with no images
    // - No .sdr folder exists

    #endregion
}
