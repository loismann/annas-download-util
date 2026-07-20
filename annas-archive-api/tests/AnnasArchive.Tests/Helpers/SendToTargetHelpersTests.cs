using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using Moq;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class SendToTargetHelpersTests
{
    private readonly Mock<IValidationService> _mockValidation;

    public SendToTargetHelpersTests()
    {
        _mockValidation = new Mock<IValidationService>();
        // Default setup - valid MD5 and title
        _mockValidation.Setup(v => v.IsValidMd5(It.IsAny<string>())).Returns(true);
        _mockValidation.Setup(v => v.IsValidTitle(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
    }

    #region ValidateSendParameters Tests

    [Fact]
    public void ValidateSendParameters_ReturnsNull_WhenAllParametersValid()
    {
        var result = SendToTargetHelpers.ValidateSendParameters(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParameters_ReturnsError_WhenMd5Invalid()
    {
        _mockValidation.Setup(v => v.IsValidMd5(It.IsAny<string>())).Returns(false);

        var result = SendToTargetHelpers.ValidateSendParameters(
            "invalid",
            "Test Book",
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("MD5", result);
    }

    [Fact]
    public void ValidateSendParameters_ReturnsError_WhenTitleTooLong()
    {
        _mockValidation.Setup(v => v.IsValidTitle(It.IsAny<string>(), It.IsAny<int>())).Returns(false);

        var result = SendToTargetHelpers.ValidateSendParameters(
            "d41d8cd98f00b204e9800998ecf8427e",
            new string('a', 600),
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("Title", result);
    }

    #endregion

    #region ValidateSendParametersExtended Tests

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenAllParametersValid()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            "https://example.com/cover.jpg",
            "John Doe",
            "1024000",
            "A great book",
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenOptionalParametersNull()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            null,
            null,
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenMd5Invalid()
    {
        _mockValidation.Setup(v => v.IsValidMd5(It.IsAny<string>())).Returns(false);

        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "invalid",
            "Test Book",
            null,
            null,
            null,
            null,
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("MD5", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenCoverUrlInvalid()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            "not-a-valid-url",
            null,
            null,
            null,
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("coverUrl", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenCoverUrlEmpty()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            "",
            null,
            null,
            null,
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenAuthorsTooLong()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            new string('a', 1001),
            null,
            null,
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("authors", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenAuthorsAtMaxLength()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            new string('a', 1000),
            null,
            null,
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenFileSizeNotNumeric()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            "not-a-number",
            null,
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("fileSize", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenFileSizeNegative()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            "-1",
            null,
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("fileSize", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenFileSizeZero()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            "0",
            null,
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenFileSizeLargeNumber()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            "9999999999999",
            null,
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsError_WhenDescriptionTooLong()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            null,
            new string('a', 5001),
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("description", result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ReturnsNull_WhenDescriptionAtMaxLength()
    {
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "d41d8cd98f00b204e9800998ecf8427e",
            "Test Book",
            null,
            null,
            null,
            new string('a', 5000),
            _mockValidation.Object);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSendParametersExtended_ChecksBaseValidationFirst()
    {
        _mockValidation.Setup(v => v.IsValidMd5(It.IsAny<string>())).Returns(false);

        // Even with invalid coverUrl, MD5 error should be returned first
        var result = SendToTargetHelpers.ValidateSendParametersExtended(
            "invalid",
            "Test Book",
            "not-a-url",
            new string('a', 2000),
            "not-a-number",
            new string('a', 10000),
            _mockValidation.Object);

        Assert.NotNull(result);
        Assert.Contains("MD5", result);
    }

    #endregion

    #region ValidateKindleTarget Tests

    [Fact]
    public void ValidateKindleTarget_ReturnsNull_WhenTargetIsDad()
    {
        var result = SendToTargetHelpers.ValidateKindleTarget("dad");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateKindleTarget_ReturnsNull_WhenTargetIsMom()
    {
        var result = SendToTargetHelpers.ValidateKindleTarget("mom");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateKindleTarget_ReturnsError_WhenTargetIsNull()
    {
        var result = SendToTargetHelpers.ValidateKindleTarget(null);
        Assert.NotNull(result);
        Assert.Contains("target", result);
    }

    [Fact]
    public void ValidateKindleTarget_ReturnsError_WhenTargetIsEmpty()
    {
        var result = SendToTargetHelpers.ValidateKindleTarget("");
        Assert.NotNull(result);
        Assert.Contains("target", result);
    }

    [Fact]
    public void ValidateKindleTarget_ReturnsError_WhenTargetIsInvalid()
    {
        var result = SendToTargetHelpers.ValidateKindleTarget("sister");
        Assert.NotNull(result);
        Assert.Contains("target", result);
    }

    #endregion

    #region GetEbookContentType Tests

    [Theory]
    [InlineData("book.epub", "application/epub+zip")]
    [InlineData("book.pdf", "application/pdf")]
    [InlineData("book.mobi", "application/x-mobipocket-ebook")]
    [InlineData("book.azw3", "application/vnd.amazon.ebook")]
    [InlineData("book.azw", "application/vnd.amazon.ebook")]
    [InlineData("book.kfx", "application/vnd.amazon.ebook")]
    [InlineData("book.fb2", "text/xml")]
    [InlineData("book.unknown", "application/octet-stream")]
    [InlineData("book.txt", "application/octet-stream")]
    public void GetEbookContentType_ReturnsCorrectContentType(string fileName, string expectedContentType)
    {
        var result = SendToTargetHelpers.GetEbookContentType(fileName);
        Assert.Equal(expectedContentType, result);
    }

    [Theory]
    [InlineData("book.EPUB", "application/epub+zip")]
    [InlineData("book.PDF", "application/pdf")]
    [InlineData("book.Mobi", "application/x-mobipocket-ebook")]
    public void GetEbookContentType_IsCaseInsensitive(string fileName, string expectedContentType)
    {
        var result = SendToTargetHelpers.GetEbookContentType(fileName);
        Assert.Equal(expectedContentType, result);
    }

    #endregion

    #region GetDropboxFolderForKindleTarget Tests

    [Fact]
    public void GetDropboxFolderForKindleTarget_ReturnsDadFolder_WhenTargetIsDad()
    {
        var result = SendToTargetHelpers.GetDropboxFolderForKindleTarget("dad");
        Assert.Equal("/dad_downloads", result);
    }

    [Fact]
    public void GetDropboxFolderForKindleTarget_ReturnsMomFolder_WhenTargetIsMom()
    {
        var result = SendToTargetHelpers.GetDropboxFolderForKindleTarget("mom");
        Assert.Equal("/mom_downloads", result);
    }

    [Fact]
    public void GetDropboxFolderForKindleTarget_IsCaseInsensitive()
    {
        var result = SendToTargetHelpers.GetDropboxFolderForKindleTarget("DAD");
        Assert.Equal("/dad_downloads", result);
    }

    #endregion
}
