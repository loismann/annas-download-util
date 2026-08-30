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

    #region Paging

    /// <summary>
    /// LibGen serves 25 rows per page and offers no way to ask for more in one
    /// request, so the page has to reach the upstream URL. Applied to an
    /// already-fetched batch instead, every page would be a copy of the first —
    /// and the caller fetches page 2 in the background and appends it, so the
    /// symptom would have been a result list of duplicates rather than an error.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WhenAskedForALaterPage_ShouldRequestThatPageUpstream()
    {
        var requested = new List<string>();
        RespondWith(CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab"), requested);

        await _service.SearchAsync("test", limit: 10, exact: false, page: 3);

        Assert.Contains(requested, url => url.Contains("page=3"));
    }

    /// <summary>
    /// Omitted rather than sent as <c>page=1</c>: the two mean the same thing to
    /// LibGen but not to the cache in front of it, and the bare URL is the one
    /// every other client asks for.
    /// </summary>
    [Fact]
    public async Task SearchAsync_OnTheFirstPage_ShouldNotSendAPageParameterAtAll()
    {
        var requested = new List<string>();
        RespondWith(CreateGeneralSearchHtml("Test Book", "abc123def456789012345678901234ab"), requested);

        await _service.SearchAsync("test", limit: 10);

        Assert.All(requested, url => Assert.DoesNotContain("page=", url));
    }

    private void RespondWith(string html, List<string> requested) =>
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                requested.Add(req.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
            });

    #endregion

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

    /// <summary>
    /// These two used to assert an empty list, and that was right while LibGen
    /// was one source among several — an unreachable mirror simply meant asking
    /// somewhere else. It is wrong now that LibGen is the only place book search
    /// looks: "every domain refused" and "nothing matched that title" would
    /// arrive at the caller as the same answer, and the reader would be told
    /// their book does not exist when the truth is that nothing was asked.
    ///
    /// <para>That is not hypothetical. Anna's Archive went behind DDoS-Guard on
    /// 2026-08-13 and the failure presented as searches quietly finding nothing
    /// for six days. A silent failure that looks like a successful empty result
    /// is the most expensive shape a failure can take, and this is the assertion
    /// that stops book search taking it again.</para>
    /// </summary>
    [Fact]
    public async Task SearchAsync_WhenEveryDomainRefuses_ShouldFailRatherThanLookEmpty()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.SearchAsync("test", limit: 10));
    }

    [Fact]
    public async Task SearchAsync_WhenEveryDomainIsUnreachable_ShouldFailRatherThanLookEmpty()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.SearchAsync("test", limit: 10));
    }

    /// <summary>The other side of it: reachable, parsed, genuinely nothing there.</summary>
    [Fact]
    public async Task SearchAsync_WhenTheSiteAnswersWithNoRows_ShouldReturnEmpty()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><p>Nothing found</p></body></html>")
            });

        Assert.Empty(await _service.SearchAsync("a book nobody has", limit: 10));
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
