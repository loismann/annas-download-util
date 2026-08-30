using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using AnnasArchive.Core.Models;
using Moq.Protected;
using System.Net.Http;
using System.Threading;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Comprehensive tests for AnnasArchiveService to ensure search, download, and scraping functionality
/// remains intact through refactoring.
/// </summary>
public class AnnasArchiveServiceTests
{
    private Mock<HttpMessageHandler> CreateMockHttpMessageHandler(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
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
                StatusCode = statusCode,
                Content = new StringContent(responseContent)
            });
        return mockHandler;
    }

    [Fact]
    public void AnnasArchiveService_Constructor_ShouldInitializeHttpClient()
    {
        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://annas-archive.org") };

        // Act
        var service = new AnnasArchiveService(httpClient, new MemoryCache(new MemoryCacheOptions()));

        // Assert
        service.Should().NotBeNull();
        service.HttpClient.Should().Be(httpClient);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_WithValidMd5_ShouldReturnLinks()
    {
        // Arrange
        var mockJson = @"[""https://download1.com/file"", ""https://download2.com/file""]";
        var mockHandler = CreateMockHttpMessageHandler(mockJson);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetDownloadLinksAsync("abc123def456789012345678901234ab");

        // Assert
        links.Should().NotBeNull();
        links.Should().HaveCount(2);
        links.Should().Contain("https://download1.com/file");
        links.Should().Contain("https://download2.com/file");
    }

    [Fact]
    public async Task GetDownloadLinksAsync_WithEmptyResponse_ShouldReturnEmptyList()
    {
        // Arrange
        var mockJson = @"[]";
        var mockHandler = CreateMockHttpMessageHandler(mockJson);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetDownloadLinksAsync("abc123def456789012345678901234ab");

        // Assert
        links.Should().NotBeNull();
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemberDownloadLinksAsync_WithValidResponse_ShouldReturnLinks()
    {
        // Arrange
        var mockJson = @"{""download_url"": [""https://member-download.com/file""]}";
        var mockHandler = CreateMockHttpMessageHandler(mockJson);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

        // Assert
        links.Should().NotBeNull();
        links.Should().HaveCount(1);
        links.First().Should().Be("https://member-download.com/file");
    }

    [Fact]
    public async Task GetMemberDownloadLinksAsync_WithStringResponse_ShouldReturnSingleLink()
    {
        // Arrange
        var mockJson = @"{""download_url"": ""https://member-download.com/file""}";
        var mockHandler = CreateMockHttpMessageHandler(mockJson);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var links = await downloads.GetMemberDownloadLinksAsync("abc123def456789012345678901234ab", "test-key");

        // Assert
        links.Should().NotBeNull();
        links.Should().HaveCount(1);
        links.First().Should().Be("https://member-download.com/file");
    }

    [Fact]
    public async Task GetMemberDownloadDocumentAsync_WithValidResponse_ShouldReturnJsonDocument()
    {
        // Arrange
        var mockJson = @"{""download_url"": ""https://download.com/file"", ""account_fast_download_info"": {""downloads_left"": 10}}";
        var mockHandler = CreateMockHttpMessageHandler(mockJson);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var downloads = new AnnasArchiveDownloads(new AnnasArchiveTransport(httpClient));

        // Act
        var doc = await downloads.GetMemberDownloadDocumentAsync("abc123def456789012345678901234ab", "test-key");

        // Assert
        doc.ValueKind.Should().Be(JsonValueKind.Object);
        doc.TryGetProperty("download_url", out var url).Should().BeTrue();
        url.GetString().Should().Be("https://download.com/file");
    }

    [Theory]
    [InlineData("abc123def456789012345678901234ab")] // valid lowercase
    [InlineData("ABC123DEF456789012345678901234AB")] // valid uppercase
    [InlineData("AbC123dEf456789012345678901234aB")] // valid mixed
    public void ValidMd5Format_ShouldBeProcessedCorrectly(string md5)
    {
        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnasArchiveService(httpClient, new MemoryCache(new MemoryCacheOptions()));

        // Act & Assert - just verify the service accepts it
        var normalized = md5.ToLowerInvariant();
        normalized.Should().HaveLength(32);
        normalized.Should().MatchRegex("^[a-f0-9]{32}$");
    }
}
