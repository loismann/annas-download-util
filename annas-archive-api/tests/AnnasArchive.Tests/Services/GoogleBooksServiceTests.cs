using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using Xunit;

namespace AnnasArchive.Tests.Services;

public class GoogleBooksServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpFactory;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly IMemoryCache _cache;
    private readonly GoogleBooksService _service;

    public GoogleBooksServiceTests()
    {
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _mockHttpFactory = new Mock<IHttpClientFactory>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        var httpClient = new HttpClient(_mockHttpHandler.Object)
        {
            BaseAddress = new Uri("https://www.googleapis.com/")
        };

        _mockHttpFactory
            .Setup(x => x.CreateClient("GoogleBooks"))
            .Returns(httpClient);

        _service = new GoogleBooksService(_mockHttpFactory.Object, _cache);
    }

    #region GetBookDescriptionAsync Tests

    [Fact]
    public async Task GetBookDescriptionAsync_WithValidResponse_ShouldReturnDescription()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        title = "Test Book",
                        authors = new[] { "Test Author" },
                        description = "This is a fascinating book about testing."
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("This is a fascinating book about testing.", result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WithISBN_ShouldUseISBNQuery()
    {
        // Arrange
        string? capturedUrl = null;
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        description = "ISBN-based description."
                    }
                }
            }
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                capturedUrl = req.RequestUri?.ToString();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(apiResponse))
            });

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author", "9780123456789");

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.Contains("isbn:9780123456789", capturedUrl);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WithNoItems_ShouldReturnNull()
    {
        // Arrange
        var apiResponse = new { items = Array.Empty<object>() };
        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Unknown Book", "Unknown Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WithEmptyDescription_ShouldReturnNull()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        title = "Test Book",
                        description = ""
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WithNoDescription_ShouldReturnNull()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        title = "Test Book",
                        authors = new[] { "Test Author" }
                        // No description field
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WhenApiFails_ShouldReturnNull()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.InternalServerError, new { });

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBookDescriptionAsync_WhenExceptionThrown_ShouldReturnNull()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await _service.GetBookDescriptionAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GetCoverUrlAsync Tests

    [Fact]
    public async Task GetCoverUrlAsync_WithThumbnail_ShouldReturnUrl()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new
                        {
                            thumbnail = "http://books.google.com/books/content?id=abc123"
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book", "Test Author");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("https://", result); // Should upgrade to HTTPS
    }

    [Fact]
    public async Task GetCoverUrlAsync_WithSmallThumbnailOnly_ShouldReturnUrl()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new
                        {
                            smallThumbnail = "http://books.google.com/books/content?id=abc123"
                        }
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book", "Test Author");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_WithEmptyTitle_ShouldReturnNull()
    {
        // Act
        var result = await _service.GetCoverUrlAsync("", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_WithWhitespaceTitle_ShouldReturnNull()
    {
        // Act
        var result = await _service.GetCoverUrlAsync("   ", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_WithNoImageLinks_ShouldReturnNull()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        title = "Test Book"
                        // No imageLinks
                    }
                }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book", "Test Author");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_WhenFirstRequestFails_ShouldTryTitleVariations()
    {
        // Arrange
        var callCount = 0;
        var apiResponse = new
        {
            items = new[]
            {
                new
                {
                    volumeInfo = new
                    {
                        imageLinks = new
                        {
                            thumbnail = "http://books.google.com/cover.jpg"
                        }
                    }
                }
            }
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                callCount++;
                // First calls fail, later one succeeds
                if (callCount <= 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(apiResponse))
                };
            });

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book: A Subtitle [Series Book 1]", "Test Author");

        // Assert - multiple variations should be tried
        Assert.True(callCount >= 2);
    }

    #endregion

    #region GetCoverCandidatesAsync Tests

    [Fact]
    public async Task GetCoverCandidatesAsync_WithMultipleResults_ShouldReturnAll()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://cover1.jpg" } } },
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://cover2.jpg" } } },
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://cover3.jpg" } } }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book", "Test Author");

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ShouldUpgradeToHttps()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://books.google.com/cover.jpg" } } }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Single(results);
        Assert.StartsWith("https://", results[0]);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ShouldRemoveDuplicates()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://same-cover.jpg" } } },
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://same-cover.jpg" } } },
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://same-cover.jpg" } } }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Single(results); // Duplicates removed
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_WithEmptyTitle_ShouldReturnEmpty()
    {
        // Act
        var results = await _service.GetCoverCandidatesAsync("");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_WhenApiFails_ShouldReturnEmpty()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.InternalServerError, new { });

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ShouldRespectLimit()
    {
        // Arrange
        var items = Enumerable.Range(1, 20).Select(i => new
        {
            volumeInfo = new { imageLinks = new { thumbnail = $"http://cover{i}.jpg" } }
        }).ToArray();

        var apiResponse = new { items };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book", limit: 5);

        // Assert
        Assert.True(results.Count <= 5);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ShouldModifyZoomParameter()
    {
        // Arrange
        var apiResponse = new
        {
            items = new[]
            {
                new { volumeInfo = new { imageLinks = new { thumbnail = "http://books.google.com/cover.jpg?zoom=1" } } }
            }
        };

        SetupHttpResponse(HttpStatusCode.OK, apiResponse);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Single(results);
        Assert.Contains("zoom=0", results[0]); // zoom=1 should be changed to zoom=0
    }

    #endregion

    #region Helper Methods

    private void SetupHttpResponse(HttpStatusCode statusCode, object responseBody)
    {
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(responseBody))
            });
    }

    #endregion
}
