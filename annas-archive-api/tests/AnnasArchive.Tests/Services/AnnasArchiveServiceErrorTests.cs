using AnnasArchive.Core.Services;
using AnnasArchive.Core.Models;
using Moq.Protected;
using System.Net.Http;
using System.Threading;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Error handling and edge case tests for AnnaArchiveService.
/// These tests cover network failures, timeouts, malformed responses, etc.
/// </summary>
public class AnnasArchiveServiceErrorTests
{
    [Fact]
    public async Task SearchAsync_WithHttpError_ShouldThrowHttpRequestException()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("Server Error")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var act = async () => await service.SearchAsync("test", limit: 10);

        // Assert - Should throw when server returns 500
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SearchAsync_WithNetworkTimeout_ShouldThrowTaskCanceledException()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new TaskCanceledException("Request timeout"));

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("https://annas-archive.org"),
            Timeout = TimeSpan.FromSeconds(1)
        };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var act = async () => await service.SearchAsync("test", limit: 10);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task GetDownloadLinksAsync_WithMalformedJson_ShouldThrowJsonException()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{ invalid json [")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var act = async () => await service.GetDownloadLinksAsync("abc123def456789012345678901234ab");

        // Assert
        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task GetDownloadLinksAsync_WithNullResponse_ShouldReturnEmptyList()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("null")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var links = await service.GetDownloadLinksAsync("abc123def456789012345678901234ab");

        // Assert
        links.Should().NotBeNull();
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemberDownloadLinksAsync_WithMissingDownloadUrl_ShouldReturnEmptyList()
    {
        // Arrange
        var mockJson = @"{""account_fast_download_info"": {""downloads_left"": 10}}";
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(mockJson)
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var links = await service.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

        // Assert
        links.Should().NotBeNull();
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemberDownloadLinksAsync_WithEmptyStringUrl_ShouldNotIncludeInResult()
    {
        // Arrange
        var mockJson = @"{""download_url"": """"}";
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(mockJson)
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var links = await service.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

        // Assert
        links.Should().NotBeNull();
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemberDownloadDocumentAsync_WithInvalidJson_ShouldThrowJsonException()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("undefined")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var act = async () => await service.GetMemberDownloadDocumentAsync("abc123def456789012345678901234ab", "test-key");

        // Assert - JSON parsing fails before we can check ValueKind
        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task SearchAsync_WithZeroLimit_ShouldReturnEmptyList()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html><body>Books</body></html>")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 0);

        // Assert
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithNegativeLimit_ShouldReturnEmptyList()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html><body>Books</body></html>")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: -1);

        // Assert
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SearchAsync_WithWhitespaceQuery_ShouldStillMakeRequest(string query)
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html><body></body></html>")
            });

        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync(query, limit: 10);

        // Assert
        results.Should().NotBeNull();
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public void HttpClient_ShouldBeExposed()
    {
        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://annas-archive.org") };

        // Act
        var service = new AnnaArchiveService(httpClient);

        // Assert
        service.HttpClient.Should().NotBeNull();
        service.HttpClient.Should().BeSameAs(httpClient);
    }
}
