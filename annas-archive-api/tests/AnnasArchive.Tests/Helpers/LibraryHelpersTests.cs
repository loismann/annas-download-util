using System.Security.Claims;
using AnnasArchive.API.Helpers;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class LibraryHelpersTests
{
    #region GetKindleTargetTag Tests

    [Fact]
    public void GetKindleTargetTag_ReturnsMomsBooks_WhenTargetIsMom()
    {
        var result = LibraryHelpers.GetKindleTargetTag("mom");
        Assert.Equal("Mom's Books", result);
    }

    [Fact]
    public void GetKindleTargetTag_ReturnsMomsBooks_WhenTargetIsMomUppercase()
    {
        var result = LibraryHelpers.GetKindleTargetTag("MOM");
        Assert.Equal("Mom's Books", result);
    }

    [Fact]
    public void GetKindleTargetTag_ReturnsDadsBooks_WhenTargetIsDad()
    {
        var result = LibraryHelpers.GetKindleTargetTag("dad");
        Assert.Equal("Dad's Books", result);
    }

    [Fact]
    public void GetKindleTargetTag_ReturnsDadsBooks_WhenTargetIsAnythingElse()
    {
        var result = LibraryHelpers.GetKindleTargetTag("other");
        Assert.Equal("Dad's Books", result);
    }

    #endregion

    #region NormalizeLibraryCoverUrl Tests

    [Fact]
    public void NormalizeLibraryCoverUrl_ReturnsNull_WhenCoverValueIsNull()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl(null, "https://example.com");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_ReturnsNull_WhenCoverValueIsEmpty()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("", "https://example.com");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_ReturnsNull_WhenCoverValueIsWhitespace()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("   ", "https://example.com");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_ReturnsOriginal_WhenAlreadyHttpUrl()
    {
        var url = "https://covers.openlibrary.org/b/id/12345-L.jpg";
        var result = LibraryHelpers.NormalizeLibraryCoverUrl(url, "https://example.com");
        Assert.Equal(url, result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_ReturnsOriginal_WhenAlreadyHttpUrlLowercase()
    {
        var url = "http://example.com/cover.jpg";
        var result = LibraryHelpers.NormalizeLibraryCoverUrl(url, "https://api.example.com");
        Assert.Equal(url, result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_BuildsApiUrl_ForRelativePath()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("_covers/book.cover.jpg", "https://api.example.com");
        Assert.Equal("https://api.example.com/api/library/cover/_covers/book.cover.jpg", result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_TrimsLeadingSlash_FromRelativePath()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("/_covers/book.cover.jpg", "https://api.example.com");
        Assert.Equal("https://api.example.com/api/library/cover/_covers/book.cover.jpg", result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_ConvertsBackslashes_ToForwardSlashes()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("_covers\\book.cover.jpg", "https://api.example.com");
        Assert.Equal("https://api.example.com/api/library/cover/_covers/book.cover.jpg", result);
    }

    [Fact]
    public void NormalizeLibraryCoverUrl_EncodesSpecialCharacters_InPath()
    {
        var result = LibraryHelpers.NormalizeLibraryCoverUrl("_covers/book with spaces.jpg", "https://api.example.com");
        Assert.Equal("https://api.example.com/api/library/cover/_covers/book%20with%20spaces.jpg", result);
    }

    #endregion

    #region ResolveUserLibraryTag Tests

    [Fact]
    public void ResolveUserLibraryTag_ReturnsNull_WhenContextUserIsNull()
    {
        var httpContext = new DefaultHttpContext();
        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsNull_WhenNameClaimIsEmpty()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsPaulsBooks_WhenNameContainsPaul()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "Paul Ferrer") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Equal("Paul's Books", result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsPaulsBooks_WhenNameContainsPaulCaseInsensitive()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "PAUL") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Equal("Paul's Books", result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsMomsBooks_WhenNameContainsMom()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "Mom User") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Equal("Mom's Books", result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsDadsBooks_WhenNameContainsDad()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "Dad User") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Equal("Dad's Books", result);
    }

    [Fact]
    public void ResolveUserLibraryTag_ReturnsNull_WhenNameDoesNotMatchAnyUser()
    {
        var httpContext = new DefaultHttpContext();
        var claims = new[] { new Claim(ClaimTypes.Name, "Unknown User") };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        var result = LibraryHelpers.ResolveUserLibraryTag(httpContext);
        Assert.Null(result);
    }

    #endregion

    #region CreateLibraryJsonOptions Tests

    [Fact]
    public void CreateLibraryJsonOptions_UsesCamelCaseNaming()
    {
        var options = LibraryHelpers.CreateLibraryJsonOptions();
        Assert.Equal(System.Text.Json.JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);
    }

    [Fact]
    public void CreateLibraryJsonOptions_WritesIndented()
    {
        var options = LibraryHelpers.CreateLibraryJsonOptions();
        Assert.True(options.WriteIndented);
    }

    #endregion

    #region FormatFileSize Tests

    [Fact]
    public void FormatFileSize_ReturnsZeroB_WhenBytesIsZero()
    {
        var result = LibraryHelpers.FormatFileSize(0);
        Assert.Equal("0B", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsZeroB_WhenBytesIsNegative()
    {
        var result = LibraryHelpers.FormatFileSize(-100);
        Assert.Equal("0B", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsBytes_WhenUnder1KB()
    {
        var result = LibraryHelpers.FormatFileSize(500);
        Assert.Equal("500.0B", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsKB_When1024Bytes()
    {
        var result = LibraryHelpers.FormatFileSize(1024);
        Assert.Equal("1.0KB", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsKB_WhenUnder1MB()
    {
        var result = LibraryHelpers.FormatFileSize(500 * 1024);
        Assert.Equal("500.0KB", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsMB_When1MBBytes()
    {
        var result = LibraryHelpers.FormatFileSize(1024 * 1024);
        Assert.Equal("1.0MB", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsMB_WhenUnder1GB()
    {
        var result = LibraryHelpers.FormatFileSize(500L * 1024 * 1024);
        Assert.Equal("500.0MB", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsGB_When1GBBytes()
    {
        var result = LibraryHelpers.FormatFileSize(1024L * 1024 * 1024);
        Assert.Equal("1.0GB", result);
    }

    [Fact]
    public void FormatFileSize_ReturnsGB_ForLargeFiles()
    {
        var result = LibraryHelpers.FormatFileSize(5L * 1024 * 1024 * 1024);
        Assert.Equal("5.0GB", result);
    }

    [Fact]
    public void FormatFileSize_FormatsWithOneDecimal()
    {
        var result = LibraryHelpers.FormatFileSize(1536); // 1.5 KB
        Assert.Equal("1.5KB", result);
    }

    #endregion
}
