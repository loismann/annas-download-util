using AnnasArchive.API.Helpers;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class ValidationHelpersTests
{
    #region ValidateStringLength Tests

    [Fact]
    public void ValidateStringLength_ReturnsNull_WhenValueIsNull()
    {
        var result = ValidationHelpers.ValidateStringLength(null, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateStringLength_ReturnsNull_WhenValueIsEmpty()
    {
        var result = ValidationHelpers.ValidateStringLength("", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateStringLength_ReturnsNull_WhenValueIsWithinLimit()
    {
        var result = ValidationHelpers.ValidateStringLength("hello", "test", 10);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateStringLength_ReturnsNull_WhenValueIsExactlyAtLimit()
    {
        var result = ValidationHelpers.ValidateStringLength("hello", "test", 5);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateStringLength_ReturnsBadRequest_WhenValueExceedsLimit()
    {
        var result = ValidationHelpers.ValidateStringLength("hello world", "testParam", 5);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateStringLength_UsesDefaultMaxLength_WhenNotSpecified()
    {
        var longString = new string('a', 501);
        var result = ValidationHelpers.ValidateStringLength(longString, "test");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidateUrl Tests

    [Fact]
    public void ValidateUrl_ReturnsNull_WhenUrlIsNull()
    {
        var result = ValidationHelpers.ValidateUrl(null, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUrl_ReturnsNull_WhenUrlIsEmpty()
    {
        var result = ValidationHelpers.ValidateUrl("", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUrl_ReturnsNull_WhenUrlIsValidHttp()
    {
        var result = ValidationHelpers.ValidateUrl("http://example.com", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUrl_ReturnsNull_WhenUrlIsValidHttps()
    {
        var result = ValidationHelpers.ValidateUrl("https://example.com/path?query=1", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUrl_ReturnsBadRequest_WhenUrlIsInvalid()
    {
        var result = ValidationHelpers.ValidateUrl("not-a-url", "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateUrl_ReturnsBadRequest_WhenUrlIsRelative()
    {
        var result = ValidationHelpers.ValidateUrl("/relative/path", "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidateNonNegativeInt Tests

    [Fact]
    public void ValidateNonNegativeInt_ReturnsNull_WhenValueIsZero()
    {
        var result = ValidationHelpers.ValidateNonNegativeInt(0, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateNonNegativeInt_ReturnsNull_WhenValueIsPositive()
    {
        var result = ValidationHelpers.ValidateNonNegativeInt(100, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateNonNegativeInt_ReturnsBadRequest_WhenValueIsNegative()
    {
        var result = ValidationHelpers.ValidateNonNegativeInt(-1, "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidatePositiveInt Tests

    [Fact]
    public void ValidatePositiveInt_ReturnsNull_WhenValueIsPositive()
    {
        var result = ValidationHelpers.ValidatePositiveInt(1, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidatePositiveInt_ReturnsBadRequest_WhenValueIsZero()
    {
        var result = ValidationHelpers.ValidatePositiveInt(0, "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidatePositiveInt_ReturnsBadRequest_WhenValueIsNegative()
    {
        var result = ValidationHelpers.ValidatePositiveInt(-5, "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidateFilePath Tests

    [Fact]
    public void ValidateFilePath_ReturnsNull_WhenPathIsNull()
    {
        var result = ValidationHelpers.ValidateFilePath(null, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFilePath_ReturnsNull_WhenPathIsEmpty()
    {
        var result = ValidationHelpers.ValidateFilePath("", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFilePath_ReturnsNull_WhenPathIsValid()
    {
        var result = ValidationHelpers.ValidateFilePath("folder/subfolder/file.txt", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFilePath_ReturnsBadRequest_WhenPathContainsDoubleDots()
    {
        var result = ValidationHelpers.ValidateFilePath("../etc/passwd", "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateFilePath_ReturnsBadRequest_WhenPathContainsDoubleSlashes()
    {
        var result = ValidationHelpers.ValidateFilePath("folder//file.txt", "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateFilePath_ReturnsBadRequest_WhenPathIsAbsolute()
    {
        var result = ValidationHelpers.ValidateFilePath("/etc/passwd", "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidateFileName Tests

    [Fact]
    public void ValidateFileName_ReturnsNull_WhenFileNameIsNull()
    {
        var result = ValidationHelpers.ValidateFileName(null, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFileName_ReturnsNull_WhenFileNameIsEmpty()
    {
        var result = ValidationHelpers.ValidateFileName("", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFileName_ReturnsNull_WhenFileNameIsValid()
    {
        var result = ValidationHelpers.ValidateFileName("myfile.txt", "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateFileName_ReturnsBadRequest_WhenFileNameContainsDoubleDots()
    {
        var result = ValidationHelpers.ValidateFileName("..passwd", "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateFileName_ReturnsBadRequest_WhenFileNameContainsForwardSlash()
    {
        var result = ValidationHelpers.ValidateFileName("folder/file.txt", "testParam");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateFileName_ReturnsBadRequest_WhenFileNameContainsBackslash()
    {
        var result = ValidationHelpers.ValidateFileName("folder\\file.txt", "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region ValidateNonNegativeLong Tests

    [Fact]
    public void ValidateNonNegativeLong_ReturnsNull_WhenValueIsZero()
    {
        var result = ValidationHelpers.ValidateNonNegativeLong(0L, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateNonNegativeLong_ReturnsNull_WhenValueIsPositive()
    {
        var result = ValidationHelpers.ValidateNonNegativeLong(1000000000L, "test");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateNonNegativeLong_ReturnsBadRequest_WhenValueIsNegative()
    {
        var result = ValidationHelpers.ValidateNonNegativeLong(-1L, "testParam");
        Assert.NotNull(result);
    }

    #endregion

    #region CombineValidations Tests

    [Fact]
    public void CombineValidations_ReturnsNull_WhenAllValidationsPass()
    {
        var result = ValidationHelpers.CombineValidations(
            ValidationHelpers.ValidateStringLength("hello", "test1"),
            ValidationHelpers.ValidateUrl("https://example.com", "test2"),
            ValidationHelpers.ValidateNonNegativeInt(5, "test3")
        );
        Assert.Null(result);
    }

    [Fact]
    public void CombineValidations_ReturnsFirstError_WhenFirstValidationFails()
    {
        var result = ValidationHelpers.CombineValidations(
            ValidationHelpers.ValidateStringLength("toolong", "test1", 3),
            ValidationHelpers.ValidateUrl("invalid", "test2")
        );
        Assert.NotNull(result);
    }

    [Fact]
    public void CombineValidations_ReturnsFirstError_WhenMiddleValidationFails()
    {
        var result = ValidationHelpers.CombineValidations(
            ValidationHelpers.ValidateStringLength("ok", "test1"),
            ValidationHelpers.ValidateUrl("invalid", "test2"),
            ValidationHelpers.ValidateNonNegativeInt(5, "test3")
        );
        Assert.NotNull(result);
    }

    [Fact]
    public void CombineValidations_ReturnsNull_WhenNoValidationsProvided()
    {
        var result = ValidationHelpers.CombineValidations();
        Assert.Null(result);
    }

    #endregion
}
