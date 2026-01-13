using AnnasArchive.API.Helpers;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class CoverLookupHelpersTests
{
    #region BuildCoverTitleCandidates Tests

    [Fact]
    public void BuildCoverTitleCandidates_ReturnsEmpty_WhenTitleIsEmpty()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("");
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_ReturnsEmpty_WhenTitleIsWhitespace()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_ReturnsTrimmedTitle_ForSimpleTitle()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("  The Great Gatsby  ");
        Assert.Contains("The Great Gatsby", result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_RemovesBracketedContent()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("The Great Gatsby [Illustrated Edition]");
        Assert.Contains("The Great Gatsby", result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_RemovesParenthesizedContent()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("The Great Gatsby (Penguin Classics)");
        Assert.Contains("The Great Gatsby", result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_RemovesBookNumberPattern()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("Harry Potter Book 1");
        Assert.Contains("Harry Potter", result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_IncludesColonSplitVariant()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("Harry Potter: The Philosopher's Stone");
        Assert.Contains("Harry Potter", result);
    }

    [Fact]
    public void BuildCoverTitleCandidates_ReturnsDistinctCandidates()
    {
        var result = CoverLookupHelpers.BuildCoverTitleCandidates("Simple Title");
        Assert.Equal(result.Distinct(StringComparer.OrdinalIgnoreCase).Count(), result.Count);
    }

    #endregion

    #region IsCoverSizeValid Tests

    [Fact]
    public void IsCoverSizeValid_ReturnsTrue_WhenBothDimensionsAtMinimum()
    {
        var result = CoverLookupHelpers.IsCoverSizeValid(100, 100);
        Assert.True(result);
    }

    [Fact]
    public void IsCoverSizeValid_ReturnsTrue_WhenBothDimensionsAboveMinimum()
    {
        var result = CoverLookupHelpers.IsCoverSizeValid(500, 800);
        Assert.True(result);
    }

    [Fact]
    public void IsCoverSizeValid_ReturnsFalse_WhenWidthBelowMinimum()
    {
        var result = CoverLookupHelpers.IsCoverSizeValid(99, 100);
        Assert.False(result);
    }

    [Fact]
    public void IsCoverSizeValid_ReturnsFalse_WhenHeightBelowMinimum()
    {
        var result = CoverLookupHelpers.IsCoverSizeValid(100, 99);
        Assert.False(result);
    }

    [Fact]
    public void IsCoverSizeValid_ReturnsFalse_WhenBothBelowMinimum()
    {
        var result = CoverLookupHelpers.IsCoverSizeValid(50, 50);
        Assert.False(result);
    }

    #endregion

    #region TryGetImageSize Tests

    [Fact]
    public void TryGetImageSize_ReturnsFalse_WhenDataTooShort()
    {
        var result = CoverLookupHelpers.TryGetImageSize(new byte[5], out var width, out var height);
        Assert.False(result);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void TryGetImageSize_DetectsPngDimensions()
    {
        // Minimal PNG header with IHDR chunk (200x150)
        var pngData = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, // IHDR chunk length
            0x49, 0x48, 0x44, 0x52, // IHDR
            0x00, 0x00, 0x00, 0xC8, // Width: 200
            0x00, 0x00, 0x00, 0x96, // Height: 150
            0x08, 0x06, 0x00, 0x00, 0x00 // bit depth, color type, etc.
        };

        var result = CoverLookupHelpers.TryGetImageSize(pngData, out var width, out var height);
        Assert.True(result);
        Assert.Equal(200, width);
        Assert.Equal(150, height);
    }

    [Fact]
    public void TryGetImageSize_DetectsGifDimensions()
    {
        // GIF89a header with 320x240 dimensions (little-endian)
        var gifData = new byte[]
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // GIF89a
            0x40, 0x01, // Width: 320 (little-endian: 0x0140)
            0xF0, 0x00, // Height: 240 (little-endian: 0x00F0)
            0x00, 0x00, 0x00 // padding
        };

        var result = CoverLookupHelpers.TryGetImageSize(gifData, out var width, out var height);
        Assert.True(result);
        Assert.Equal(320, width);
        Assert.Equal(240, height);
    }

    [Fact]
    public void TryGetImageSize_ReturnsFalse_ForUnknownFormat()
    {
        var unknownData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };
        var result = CoverLookupHelpers.TryGetImageSize(unknownData, out var width, out var height);
        Assert.False(result);
    }

    #endregion

    #region DetermineImageExtension Tests

    [Fact]
    public void DetermineImageExtension_ReturnsJpg_WhenUrlEndsWithJpg()
    {
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/image.jpg", Array.Empty<byte>());
        Assert.Equal(".jpg", result);
    }

    [Fact]
    public void DetermineImageExtension_ReturnsJpg_WhenUrlEndsWithJpeg()
    {
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/image.jpeg", Array.Empty<byte>());
        Assert.Equal(".jpg", result);
    }

    [Fact]
    public void DetermineImageExtension_ReturnsPng_WhenUrlEndsWithPng()
    {
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/image.png", Array.Empty<byte>());
        Assert.Equal(".png", result);
    }

    [Fact]
    public void DetermineImageExtension_ReturnsGif_WhenUrlEndsWithGif()
    {
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/image.gif", Array.Empty<byte>());
        Assert.Equal(".gif", result);
    }

    [Fact]
    public void DetermineImageExtension_DetectsPng_FromMagicBytes()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/unknown", pngBytes);
        Assert.Equal(".png", result);
    }

    [Fact]
    public void DetermineImageExtension_DetectsJpeg_FromMagicBytes()
    {
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/unknown", jpegBytes);
        Assert.Equal(".jpg", result);
    }

    [Fact]
    public void DetermineImageExtension_DetectsGif_FromMagicBytes()
    {
        var gifBytes = new byte[] { 0x47, 0x49, 0x46, 0x38 };
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/unknown", gifBytes);
        Assert.Equal(".gif", result);
    }

    [Fact]
    public void DetermineImageExtension_DefaultsToJpg_WhenUnknown()
    {
        var unknownBytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var result = CoverLookupHelpers.DetermineImageExtension("https://example.com/unknown", unknownBytes);
        Assert.Equal(".jpg", result);
    }

    #endregion
}
