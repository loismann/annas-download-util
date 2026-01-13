using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class OpenLibraryServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpFactory;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly IMemoryCache _cache;
    private readonly OpenLibraryService _service;

    public OpenLibraryServiceTests()
    {
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _mockHttpFactory = new Mock<IHttpClientFactory>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        var httpClient = new HttpClient(_mockHttpHandler.Object)
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };

        _mockHttpFactory
            .Setup(x => x.CreateClient("OpenLibrary"))
            .Returns(httpClient);

        _service = new OpenLibraryService(_mockHttpFactory.Object, _cache);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_ReturnsDescription_FromWorksAPI()
    {
        // Arrange
        var searchResponse = new
        {
            docs = new[]
            {
                new { key = "/works/OL12345W" }
            }
        };

        var worksResponse = new
        {
            description = "A fascinating story about adventure."
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", searchResponse);
        SetupHttpResponse("https://openlibrary.org/works/OL12345W.json", worksResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("A fascinating story about adventure.", result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_ReturnsDescription_FromObjectWithValue()
    {
        // Arrange
        var searchResponse = new
        {
            docs = new[]
            {
                new { key = "/works/OL12345W" }
            }
        };

        var worksResponse = new
        {
            description = new
            {
                type = "/type/text",
                value = "A story with object description format."
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", searchResponse);
        SetupHttpResponse("https://openlibrary.org/works/OL12345W.json", worksResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("A story with object description format.", result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_ReturnsNull_WhenNoWorkFound()
    {
        // Arrange
        var searchResponse = new
        {
            docs = Array.Empty<object>()
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", searchResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Nonexistent Book", "Unknown Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_UsesISBN_WhenProvided()
    {
        // Arrange
        var isbnResponse = new
        {
            works = new[]
            {
                new { key = "/works/OL12345W" }
            }
        };

        var worksResponse = new
        {
            description = "ISBN-based description."
        };

        SetupHttpResponse("https://openlibrary.org/isbn/9780123456789.json", isbnResponse);
        SetupHttpResponse("https://openlibrary.org/works/OL12345W.json", worksResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author", "9780123456789");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ISBN-based description.", result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_FallsBackToSearch_WhenISBNFails()
    {
        // Arrange
        SetupHttpResponse("https://openlibrary.org/isbn/9780123456789.json",
            statusCode: HttpStatusCode.NotFound);

        var searchResponse = new
        {
            docs = new[]
            {
                new { key = "/works/OL12345W" }
            }
        };

        var worksResponse = new
        {
            description = "Fallback description from search."
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", searchResponse);
        SetupHttpResponse("https://openlibrary.org/works/OL12345W.json", worksResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author", "9780123456789");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Fallback description from search.", result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_ReturnsNull_WhenAllSourcesFail()
    {
        // Arrange
        var searchResponse = new
        {
            docs = new[]
            {
                new { key = "/works/OL12345W" }
            }
        };

        var worksResponse = new
        {
            // No description field
            title = "Test Book"
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", searchResponse);
        SetupHttpResponse("https://openlibrary.org/works/OL12345W.json", worksResponse);
        SetupHttpResponse("https://openlibrary.org/search.json?*first_sentence*", new { docs = Array.Empty<object>() });

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    private void SetupHttpResponse<T>(string urlPattern, T responseObject, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var jsonContent = new StringContent(
            JsonSerializer.Serialize(responseObject),
            System.Text.Encoding.UTF8,
            "application/json"
        );

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null &&
                    MatchesPattern(req.RequestUri.ToString(), urlPattern)),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = jsonContent
            });
    }

    private void SetupHttpResponse(string urlPattern, HttpStatusCode statusCode)
    {
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null &&
                    MatchesPattern(req.RequestUri.ToString(), urlPattern)),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent("{}")
            });
    }

    private static bool MatchesPattern(string url, string pattern)
    {
        if (pattern.Contains("*"))
        {
            var parts = pattern.Split('*');
            return parts.All(part => url.Contains(part));
        }
        return url == pattern;
    }
}
