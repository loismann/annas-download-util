using AnnasArchive.Core.Services;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class LibGenServiceTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly LibGenService _service;

    public LibGenServiceTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object);
        _service = new LibGenService(_httpClient);
    }

    #region Mirror Fallback Tests

    [Fact]
    public async Task SearchAsync_WhenFirstDomainSucceeds_ShouldNotTryOtherDomains()
    {
        // Arrange
        var callCount = 0;
        var html = CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab");

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                };
            });

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal(1, callCount); // Only first domain was called
    }

    [Fact]
    public async Task SearchAsync_WhenFirstDomainFails_ShouldFallbackToSecondDomain()
    {
        // Arrange
        var callCount = 0;
        var html = CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab");

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callCount++;
                // First domain fails, second succeeds
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                };
            });

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal(2, callCount); // Two domains tried
    }

    [Fact]
    public async Task SearchAsync_WhenFirstTwoDomainsFail_ShouldFallbackToThirdDomain()
    {
        // Arrange
        var callCount = 0;
        var html = CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab");

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callCount++;
                // First two domains fail, third succeeds
                if (callCount <= 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                };
            });

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal(3, callCount); // Three domains tried
    }

    [Fact]
    public async Task SearchAsync_WhenDomainThrowsException_ShouldFallbackToNextDomain()
    {
        // Arrange
        var callCount = 0;
        var html = CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab");

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callCount++;
                // First domain throws, second succeeds
                if (callCount == 1)
                {
                    throw new HttpRequestException("Connection refused");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html)
                };
            });

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Single(results);
        Assert.Equal(2, callCount); // Two domains tried
    }

    [Fact]
    public async Task SearchAsync_WhenAllDomainsFail_ShouldReturnEmptyResults()
    {
        // Arrange
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WhenAllDomainsThrowExceptions_ShouldReturnEmptyResults()
    {
        // Arrange
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var results = await _service.SearchAsync("test", limit: 10);

        // Assert
        Assert.Empty(results);
    }

    #endregion

    #region Input Validation Tests

    [Fact]
    public async Task SearchAsync_WithEmptyQuery_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.SearchAsync("", limit: 10);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithWhitespaceQuery_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.SearchAsync("   ", limit: 10);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithNullQuery_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.SearchAsync(null!, limit: 10);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithZeroLimit_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.SearchAsync("test", limit: 0);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithNegativeLimit_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.SearchAsync("test", limit: -5);

        // Assert
        Assert.Empty(results);
    }

    #endregion

    #region General Search Fallback to Fiction Tests

    [Fact]
    public async Task SearchAsync_WhenGeneralReturnsNoResults_ShouldTryFictionSearch()
    {
        // Arrange
        var callUrls = new List<string>();
        var fictionHtml = CreateFictionSearchHtml("Fiction Book", "abc123def456789012345678901234ab");

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callUrls.Add(req.RequestUri!.PathAndQuery);

                // General search returns empty, fiction search has results
                if (req.RequestUri.PathAndQuery.Contains("index.php"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body></body></html>")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fictionHtml)
                };
            });

        // Act
        var results = await _service.SearchAsync("fiction book", limit: 10);

        // Assert
        Assert.Single(results);
        Assert.Contains(callUrls, u => u.Contains("index.php")); // General search was tried
        Assert.Contains(callUrls, u => u.Contains("/fiction/")); // Fiction search was tried
    }

    #endregion

    #region Download URL Tests

    [Fact]
    public async Task GetDownloadUrlAsync_WithValidMd5_ShouldReturnDownloadUrl()
    {
        // Arrange
        var downloadPageHtml = @"
            <html>
                <body>
                    <a href=""/get/ABC123"">GET</a>
                </body>
            </html>";

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(downloadPageHtml)
            });

        // Act
        var url = await _service.GetDownloadUrlAsync("abc123def456789012345678901234ab");

        // Assert
        Assert.NotNull(url);
        Assert.Contains("/get/ABC123", url);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_WhenNoDownloadLink_ShouldReturnNull()
    {
        // Arrange
        var html = "<html><body><p>No download links here</p></body></html>";

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            });

        // Act
        var url = await _service.GetDownloadUrlAsync("abc123def456789012345678901234ab");

        // Assert
        Assert.Null(url);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_WhenRequestFails_ShouldReturnNull()
    {
        // Arrange
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var url = await _service.GetDownloadUrlAsync("abc123def456789012345678901234ab");

        // Assert
        Assert.Null(url);
    }

    #endregion

    #region Helper Methods

    private static string CreateGeneralSearchHtml(string title, string md5)
    {
        return $@"
            <html>
            <body>
                <table id='tablelibgen'>
                    <tr><th>ID</th><th>Authors</th><th>Title</th><th>Publisher</th><th>Year</th><th>Pages</th><th>Language</th><th>Size</th><th>Ext</th><th>Mirrors</th></tr>
                    <tr>
                        <td>12345</td>
                        <td>Test Author</td>
                        <td><a href='/book/{md5}'>{title}</a></td>
                        <td>Test Publisher</td>
                        <td>2024</td>
                        <td>300</td>
                        <td>English</td>
                        <td>1.5 MB</td>
                        <td>epub</td>
                        <td><a href='/main/{md5}'>Mirror 1</a></td>
                    </tr>
                </table>
            </body>
            </html>";
    }

    private static string CreateFictionSearchHtml(string title, string md5)
    {
        return $@"
            <html>
            <body>
                <table class='catalog'>
                    <tbody>
                        <tr>
                            <td>Test Author</td>
                            <td>Series</td>
                            <td>{title}</td>
                            <td>English</td>
                            <td>epub/1.5 MB</td>
                            <td><a href='/main/{md5}'>Mirror</a></td>
                            <td></td>
                            <td></td>
                            <td></td>
                        </tr>
                    </tbody>
                </table>
            </body>
            </html>";
    }

    #endregion
}
