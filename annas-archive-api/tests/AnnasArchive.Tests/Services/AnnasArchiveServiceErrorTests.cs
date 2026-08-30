using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using AnnasArchive.Core.Models;
using Moq.Protected;
using System.Net.Http;
using System.Threading;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Error handling and edge case tests for AnnasArchiveService.
/// These tests cover network failures, timeouts, malformed responses, etc.
/// </summary>
public class AnnasArchiveServiceErrorTests
{
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
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var act = async () => await downloads.GetDownloadLinksAsync("abc123def456789012345678901234ab");

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
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetDownloadLinksAsync("abc123def456789012345678901234ab");

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
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

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
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

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
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var act = async () => await downloads.GetMemberDownloadDocumentAsync("abc123def456789012345678901234ab", "test-key");

        // Assert - JSON parsing fails before we can check ValueKind
        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public void HttpClient_ShouldBeExposed()
    {
        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://annas-archive.org") };

        // Act
        var service = new AnnasArchiveService(httpClient, new MemoryCache(new MemoryCacheOptions()));

        // Assert
        service.HttpClient.Should().NotBeNull();
        service.HttpClient.Should().BeSameAs(httpClient);
    }
}
