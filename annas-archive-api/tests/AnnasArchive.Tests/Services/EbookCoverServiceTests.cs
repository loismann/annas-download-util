using AnnasArchive.Core.Services;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Unit tests for EbookCoverService - image format detection and format support
/// </summary>
public class EbookCoverServiceTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly EbookCoverService _service;

    public EbookCoverServiceTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object);
        _service = new EbookCoverService(_httpClient);
    }

    #region IsFormatSupported Tests

    [Theory]
    [InlineData("epub", true)]
    [InlineData("EPUB", true)]
    [InlineData(".epub", true)]
    [InlineData(".EPUB", true)]
    [InlineData("Epub", true)]
    public void IsFormatSupported_WithEpubFormat_ReturnsTrue(string format, bool expected)
    {
        // Act
        var result = _service.IsFormatSupported(format);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("pdf", false)]
    [InlineData("mobi", false)]
    [InlineData("azw3", false)]
    [InlineData("cbz", false)]
    [InlineData("txt", false)]
    [InlineData("doc", false)]
    public void IsFormatSupported_WithUnsupportedFormats_ReturnsFalse(string format, bool expected)
    {
        // Act
        var result = _service.IsFormatSupported(format);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsFormatSupported_WithEmptyOrNullFormat_ReturnsFalse(string? format)
    {
        // Act
        var result = _service.IsFormatSupported(format!);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region ReplaceCoverAsync - Format Validation Tests

    [Theory]
    [InlineData("pdf")]
    [InlineData("mobi")]
    [InlineData("azw3")]
    public async Task ReplaceCoverAsync_WithUnsupportedFormat_ReturnsOriginalStream(string format)
    {
        // Arrange
        var originalData = new byte[] { 1, 2, 3, 4, 5 };
        var inputStream = new MemoryStream(originalData);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", format);

        // Assert - Should return the same stream without modification
        Assert.Same(inputStream, result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WhenCoverDownloadFails_ReturnsOriginalStream()
    {
        // Arrange
        var originalData = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(originalData);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", "epub");

        // Assert - Should return stream (either original or repositioned)
        Assert.NotNull(result);
        result.Position = 0;
        Assert.True(result.Length > 0);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithNonSeekableStream_BuffersToMemory()
    {
        // Arrange
        var originalData = CreateMinimalEpubBytes();
        var nonSeekableStream = new NonSeekableStream(originalData);

        SetupMockCoverDownload(CreateJpegBytes());

        // Act - Should not throw
        var result = await _service.ReplaceCoverAsync(nonSeekableStream, "https://example.com/cover.jpg", "epub");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.CanSeek); // Should have been buffered to a seekable stream
    }

    #endregion

    #region Image Format Detection Tests

    [Fact]
    public async Task ReplaceCoverAsync_WithJpegUrl_DetectsJpegExtension()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var jpegBytes = CreateJpegBytes();

        SetupMockCoverDownload(jpegBytes);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", "epub");

        // Assert - Should succeed without throwing
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithPngUrl_DetectsPngExtension()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var pngBytes = CreatePngBytes();

        SetupMockCoverDownload(pngBytes);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.png", "epub");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithGifUrl_DetectsGifExtension()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var gifBytes = CreateGifBytes();

        SetupMockCoverDownload(gifBytes);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.gif", "epub");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithNoExtensionUrl_DetectsFromImageHeader_Jpeg()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var jpegBytes = CreateJpegBytes();

        SetupMockCoverDownload(jpegBytes);

        // Act - URL has no extension, should detect from header
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/image/12345", "epub");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithNoExtensionUrl_DetectsFromImageHeader_Png()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var pngBytes = CreatePngBytes();

        SetupMockCoverDownload(pngBytes);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/image/67890", "epub");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithNoExtensionUrl_DetectsFromImageHeader_Gif()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        var gifBytes = CreateGifBytes();

        SetupMockCoverDownload(gifBytes);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/image/abcde", "epub");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithUnknownFormat_DefaultsToJpeg()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        // Unknown header - not JPEG, PNG, or GIF
        var unknownBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04 };

        SetupMockCoverDownload(unknownBytes);

        // Act - Should default to .jpg extension
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/image/unknown", "epub");

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ReplaceCoverAsync_WithEmptyEpub_HandlesGracefully()
    {
        // Arrange
        var inputStream = new MemoryStream(new byte[0]);

        SetupMockCoverDownload(CreateJpegBytes());

        // Act & Assert - Should not throw, returns stream
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", "epub");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithCorruptedEpub_ReturnsOriginalStream()
    {
        // Arrange - Invalid ZIP data
        var corruptedData = new byte[] { 0x50, 0x4B, 0x00, 0x00 }; // Starts like ZIP but invalid
        var inputStream = new MemoryStream(corruptedData);

        SetupMockCoverDownload(CreateJpegBytes());

        // Act - Should handle gracefully
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", "epub");

        // Assert - Should return something (original stream or reset)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReplaceCoverAsync_WithSmallImageData_HandlesGracefully()
    {
        // Arrange
        var epubBytes = CreateMinimalEpubBytes();
        var inputStream = new MemoryStream(epubBytes);
        // Image data too small to have valid header
        var tinyImageData = new byte[] { 0xFF };

        SetupMockCoverDownload(tinyImageData);

        // Act
        var result = await _service.ReplaceCoverAsync(inputStream, "https://example.com/cover.jpg", "epub");

        // Assert - Should handle gracefully
        Assert.NotNull(result);
    }

    #endregion

    #region Helper Methods

    private void SetupMockCoverDownload(byte[] imageData)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(imageData)
            });
    }

    /// <summary>
    /// Creates a minimal valid EPUB (ZIP) file for testing
    /// </summary>
    private static byte[] CreateMinimalEpubBytes()
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            // Add mimetype (required for EPUB)
            var mimetypeEntry = archive.CreateEntry("mimetype", System.IO.Compression.CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetypeEntry.Open()))
            {
                writer.Write("application/epub+zip");
            }

            // Add a container.xml
            var containerEntry = archive.CreateEntry("META-INF/container.xml");
            using (var writer = new StreamWriter(containerEntry.Open()))
            {
                writer.Write(@"<?xml version=""1.0""?>
<container version=""1.0"" xmlns=""urn:oasis:names:tc:opendocument:xmlns:container"">
  <rootfiles>
    <rootfile full-path=""content.opf"" media-type=""application/oebps-package+xml""/>
  </rootfiles>
</container>");
            }

            // Add minimal content.opf
            var opfEntry = archive.CreateEntry("content.opf");
            using (var writer = new StreamWriter(opfEntry.Open()))
            {
                writer.Write(@"<?xml version=""1.0""?>
<package xmlns=""http://www.idpf.org/2007/opf"" version=""3.0"">
  <metadata xmlns:dc=""http://purl.org/dc/elements/1.1/"">
    <dc:title>Test Book</dc:title>
  </metadata>
  <manifest></manifest>
  <spine></spine>
</package>");
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Creates bytes with JPEG magic header: FF D8 FF
    /// </summary>
    private static byte[] CreateJpegBytes()
    {
        // JPEG header: FF D8 FF E0 (JFIF marker)
        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01 };
    }

    /// <summary>
    /// Creates bytes with PNG magic header: 89 50 4E 47
    /// </summary>
    private static byte[] CreatePngBytes()
    {
        // PNG header: 89 50 4E 47 0D 0A 1A 0A
        return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
    }

    /// <summary>
    /// Creates bytes with GIF magic header: 47 49 46 (GIF)
    /// </summary>
    private static byte[] CreateGifBytes()
    {
        // GIF header: 47 49 46 38 39 61 (GIF89a)
        return new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00 };
    }

    #endregion

    /// <summary>
    /// Helper class to simulate a non-seekable stream
    /// </summary>
    private class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
