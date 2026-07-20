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

    #region Schema Change Tests - Author Suggestions

    [Fact]
    public async Task GetAuthorSuggestionsAsync_ReturnsAuthors_WithConfidenceLevels()
    {
        // Arrange
        var response = new
        {
            docs = new[]
            {
                new { author_name = new[] { "Stephen King", "Richard Bachman" } },
                new { author_name = new[] { "Stephen King" } },
                new { author_name = new[] { "Stephen King" } },
                new { author_name = new[] { "Stephen Edwin King" } }
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?title=*", response);

        // Act
        var results = await _service.GetAuthorSuggestionsAsync("The Shining");

        // Assert
        Assert.NotEmpty(results);
        var firstAuthor = results.First();
        Assert.Equal("Stephen King", firstAuthor.Author);
        Assert.Equal("high", firstAuthor.Confidence); // Most frequent = high confidence
    }

    [Fact]
    public async Task GetAuthorSuggestionsAsync_HandlesEmptyAuthorNames()
    {
        // Arrange - Some docs might have empty author_name arrays
        var response = new
        {
            docs = new object[]
            {
                new { author_name = Array.Empty<string>() },
                new { author_name = new[] { "Valid Author" } },
                new { title = "Book without author_name" } // Missing author_name entirely
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?title=*", response);

        // Act
        var results = await _service.GetAuthorSuggestionsAsync("Test Book");

        // Assert
        Assert.Single(results);
        Assert.Equal("Valid Author", results[0].Author);
    }

    [Fact]
    public async Task GetAuthorSuggestionsAsync_ReturnsEmpty_WhenNoDocsProperty()
    {
        // Arrange - Response might be missing docs entirely
        var response = new { numFound = 0 };

        SetupHttpResponse("https://openlibrary.org/search.json?title=*", response);

        // Act
        var results = await _service.GetAuthorSuggestionsAsync("Nonexistent Book");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAuthorSuggestionsAsync_ReturnsEmpty_WhenTitleIsEmpty()
    {
        // Act
        var results = await _service.GetAuthorSuggestionsAsync("");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAuthorSuggestionsAsync_ReturnsEmpty_WhenTitleIsWhitespace()
    {
        // Act
        var results = await _service.GetAuthorSuggestionsAsync("   ");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAuthorSuggestionsAsync_UsesCachedResults()
    {
        // Arrange
        var response = new
        {
            docs = new[]
            {
                new { author_name = new[] { "Cached Author" } }
            }
        };

        var callCount = 0;
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(JsonSerializer.Serialize(response))
                };
            });

        // Act - Call twice
        var result1 = await _service.GetAuthorSuggestionsAsync("Test Book");
        var result2 = await _service.GetAuthorSuggestionsAsync("Test Book");

        // Assert - Should only make one HTTP call due to caching
        Assert.Equal(1, callCount);
        Assert.Equal(result1[0].Author, result2[0].Author);
    }

    #endregion

    #region Schema Change Tests - Cover URL Variations

    [Fact]
    public async Task GetCoverUrlAsync_ReturnsNull_WhenTitleIsEmpty()
    {
        // Act
        var result = await _service.GetCoverUrlAsync("");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_ReturnsNull_WhenTitleIsWhitespace()
    {
        // Act
        var result = await _service.GetCoverUrlAsync("   ");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverUrlAsync_SelectsCoverWithMostEditions()
    {
        // Arrange - Multiple covers with different edition counts
        var response = new
        {
            docs = new[]
            {
                new { cover_i = 1001, edition_count = 5 },
                new { cover_i = 1002, edition_count = 100 }, // Most editions
                new { cover_i = 1003, edition_count = 25 }
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", response);

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("1002", result); // Should select cover with most editions
    }

    [Fact]
    public async Task GetCoverUrlAsync_HandlesMissingEditionCount()
    {
        // Arrange - Some docs might not have edition_count
        var response = new
        {
            docs = new object[]
            {
                new { cover_i = 1001 }, // No edition_count
                new { cover_i = 1002, edition_count = 10 }
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", response);

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("1002", result); // Should prefer the one with edition_count
    }

    [Fact]
    public async Task GetCoverUrlAsync_TriesTitleVariations()
    {
        // Arrange - First request returns no cover, second (without subtitle) succeeds
        var callCount = 0;
        var responseWithCover = new
        {
            docs = new[]
            {
                new { cover_i = 5001, edition_count = 1 }
            }
        };
        var emptyResponse = new { docs = Array.Empty<object>() };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call (full title) returns empty, second (without subtitle) has cover
                var response = callCount == 1 ? emptyResponse : (object)responseWithCover;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(JsonSerializer.Serialize(response))
                };
            });

        // Act - Title with subtitle
        var result = await _service.GetCoverUrlAsync("Test Book: A Subtitle");

        // Assert - Should try variation without subtitle
        Assert.True(callCount >= 2);
    }

    #endregion

    #region Schema Change Tests - Cover Candidates

    [Fact]
    public async Task GetCoverCandidatesAsync_ReturnsEmpty_WhenTitleIsEmpty()
    {
        // Act
        var results = await _service.GetCoverCandidatesAsync("");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ReturnsMultipleCovers_SortedByEditionCount()
    {
        // Arrange
        var response = new
        {
            docs = new[]
            {
                new { cover_i = 2001, edition_count = 5 },
                new { cover_i = 2002, edition_count = 100 },
                new { cover_i = 2003, edition_count = 50 }
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", response);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains("2002", results[0]); // Highest edition count first
        Assert.Contains("2003", results[1]); // Second highest
        Assert.Contains("2001", results[2]); // Lowest
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_RespectsLimit()
    {
        // Arrange
        var docs = Enumerable.Range(1, 20).Select(i => new { cover_i = 3000 + i, edition_count = i }).ToArray();
        var response = new { docs };

        SetupHttpResponse("https://openlibrary.org/search.json?*", response);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book", limit: 5);

        // Assert
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_DeduplicatesCovers()
    {
        // Arrange - Same cover_i appears multiple times
        var response = new
        {
            docs = new[]
            {
                new { cover_i = 4001, edition_count = 10 },
                new { cover_i = 4001, edition_count = 20 }, // Duplicate with higher count
                new { cover_i = 4002, edition_count = 5 }
            }
        };

        SetupHttpResponse("https://openlibrary.org/search.json?*", response);

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert
        Assert.Equal(2, results.Count); // Only unique covers
        Assert.Contains("4001", results[0]); // Should use the higher edition count
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetAuthorSuggestionsAsync_ReturnsEmpty_OnException()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var results = await _service.GetAuthorSuggestionsAsync("Test Book");

        // Assert - Should not throw, just return empty
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCoverUrlAsync_ReturnsNull_OnException()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await _service.GetCoverUrlAsync("Test Book");

        // Assert - Should not throw, just return null
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ReturnsEmpty_OnException()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var results = await _service.GetCoverCandidatesAsync("Test Book");

        // Assert - Should not throw, just return empty
        Assert.Empty(results);
    }

    #endregion
}
