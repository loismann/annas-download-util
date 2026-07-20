using AnnasArchive.Core.Services;
using AnnasArchive.Core.Models;
using Moq.Protected;
using System.Net.Http;
using System.Threading;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Tests for AnnaArchiveService HTML parsing to cover different book metadata formats.
/// These tests cover the BuildDtoFromAnchor method branches that parse various HTML structures.
/// </summary>
public class AnnaArchiveServiceHtmlParsingTests
{
    private Mock<HttpMessageHandler> CreateMockHandler(string html)
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
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(html)
            });
        return mockHandler;
    }

    [Fact]
    public async Task SearchAsync_WithMultipleAuthors_ShouldParseAllAuthors()
    {
        // Arrange
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'>
                    <img src='cover.jpg'>
                </a>
                <div>
                    <a class='line-clamp-[3] js-vim-focus'>Test Book</a>
                    <a class='line-clamp-[2] text-sm'><span class='icon-[mdi--user-edit]'></span>Author One; Author Two, Author Three</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1.2MB · 2023 · 📕 Book (fiction) · /lgli</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.Authors.Should().Contain("Author One");
        book.Authors.Should().Contain("Author Two");
        book.Authors.Should().Contain("Author Three");
    }

    [Fact]
    public async Task SearchAsync_WithPublisherAndYear_ShouldParseYearFromPublisher()
    {
        // Arrange
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'></a>
                <div>
                    <a class='line-clamp-[3] js-vim-focus'>Test Book</a>
                    <a class='line-clamp-[2] text-sm'><span class='icon-[mdi--company]'></span>Random House, Series Name, 2021</a>
                    <div class='text-gray-800'>English [en] · PDF · 2.5MB</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.Publisher.Should().Be("Random House");
        book.PublicationYear.Should().Be(2021);
    }

    [Fact]
    public async Task SearchAsync_WithDifferentFormats_ShouldParseCorrectly()
    {
        // Arrange - Test PDF, MOBI, AZW3
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234a1'></a>
                <div>
                    <a class='js-vim-focus'>PDF Book</a>
                    <div class='text-gray-800'>English [en] · PDF · 5.2MB</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234a2'></a>
                <div>
                    <a class='js-vim-focus'>MOBI Book</a>
                    <div class='text-gray-800'>English [en] · MOBI · 1.8MB</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234a3'></a>
                <div>
                    <a class='js-vim-focus'>AZW3 Book</a>
                    <div class='text-gray-800'>English [en] · AZW3 · 3.1MB</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 3);

        // Assert
        results.Should().HaveCount(3);
        results.ElementAt(0).Format.Should().Be("PDF");
        results.ElementAt(1).Format.Should().Be("MOBI");
        results.ElementAt(2).Format.Should().Be("AZW3");
    }

    [Fact]
    public async Task SearchAsync_WithDifferentFileSizes_ShouldParseCorrectly()
    {
        // Arrange - Test KB, MB, GB
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234b1'></a>
                <div>
                    <a class='js-vim-focus'>Small Book</a>
                    <div class='text-gray-800'>English [en] · EPUB · 125KB</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234b2'></a>
                <div>
                    <a class='js-vim-focus'>Medium Book</a>
                    <div class='text-gray-800'>English [en] · EPUB · 2.5MB</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234b3'></a>
                <div>
                    <a class='js-vim-focus'>Large Book</a>
                    <div class='text-gray-800'>English [en] · PDF · 1.2GB</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 3);

        // Assert
        results.Should().HaveCount(3);
        results.ElementAt(0).FileSize.Should().Be("125KB");
        results.ElementAt(1).FileSize.Should().Be("2.5MB");
        results.ElementAt(2).FileSize.Should().Be("1.2GB");
    }

    [Fact]
    public async Task SearchAsync_WithDifferentBookTypes_ShouldParseCorrectly()
    {
        // Arrange - Test fiction, non-fiction, magazine, comic
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234c1'></a>
                <div>
                    <a class='js-vim-focus'>Fiction Book</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · 📕 Book (fiction)</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234c2'></a>
                <div>
                    <a class='js-vim-focus'>Non-Fiction Book</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · Book (non-fiction)</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234c3'></a>
                <div>
                    <a class='js-vim-focus'>Magazine</a>
                    <div class='text-gray-800'>English [en] · PDF · 5MB · magazine</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234c4'></a>
                <div>
                    <a class='js-vim-focus'>Comic</a>
                    <div class='text-gray-800'>English [en] · CBZ · 50MB · comic</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 4);

        // Assert
        results.Should().HaveCount(4);
        results.ElementAt(0).BookType.Should().Contain("fiction");
        results.ElementAt(1).BookType.Should().Contain("non-fiction");
        results.ElementAt(2).BookType.Should().Be("magazine");
        results.ElementAt(3).BookType.Should().Be("comic");
    }

    [Fact]
    public async Task SearchAsync_WithDifferentSources_ShouldParseCorrectly()
    {
        // Arrange - Test different source formats
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234d1'></a>
                <div>
                    <a class='js-vim-focus'>Book 1</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · /lgli</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234d2'></a>
                <div>
                    <a class='js-vim-focus'>Book 2</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · /lgli/lgrs/upload/zlib</div>
                </div>
            </div>
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234d3'></a>
                <div>
                    <a class='js-vim-focus'>Book 3</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · 🚀/lgli/lgrs</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 3);

        // Assert
        results.Should().HaveCount(3);
        results.ElementAt(0).Source.Should().Be("/lgli");
        results.ElementAt(1).Source.Should().Be("/lgli/lgrs/upload/zlib");
        results.ElementAt(2).Source.Should().Be("/lgli/lgrs"); // Rocket emoji stripped
    }

    [Fact]
    public async Task SearchAsync_WithYearInMetadata_ShouldParseYear()
    {
        // Arrange - Year appears in metadata line instead of publisher
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'></a>
                <div>
                    <a class='js-vim-focus'>Test Book</a>
                    <a class='line-clamp-[2] text-sm'><span class='icon-[mdi--company]'></span>Publisher Only</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB · 2019 · Book</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.PublicationYear.Should().Be(2019);
    }

    [Fact]
    public async Task SearchAsync_WithMinimalMetadata_ShouldHandleGracefully()
    {
        // Arrange - Very minimal book data
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'></a>
                <div>
                    <a class='js-vim-focus'>Minimal Book</a>
                    <div class='text-gray-800'>English [en]</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.Title.Should().Be("Minimal Book");
        book.Language.Should().Be("English");
        book.Format.Should().BeEmpty();
        book.FileSize.Should().BeEmpty();
        book.Publisher.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithNoAuthors_ShouldReturnEmptyAuthorList()
    {
        // Arrange
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'></a>
                <div>
                    <a class='js-vim-focus'>Book Without Author</a>
                    <div class='text-gray-800'>English [en] · EPUB · 1MB</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.Authors.Should().NotBeNull();
        book.Authors.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithComplexMetadata_ShouldParseAllFields()
    {
        // Arrange - Book with all metadata fields populated
        var mockHtml = @"
            <div class='flex pt-3 pb-3 border-b'>
                <a href='/md5/abc123def456789012345678901234ab'></a>
                <div>
                    <a class='line-clamp-[3] js-vim-focus'>Complete Metadata Book</a>
                    <a class='line-clamp-[2] text-sm'><span class='icon-[mdi--user-edit]'></span>John Doe, Jane Smith</a>
                    <a class='line-clamp-[2] text-sm'><span class='icon-[mdi--company]'></span>Penguin Random House, Best Sellers Series, 2022</a>
                    <div class='text-gray-800'>English [en] · EPUB · 3.7MB · 2022 · 📕 Book (fiction) · 🚀/lgli/lgrs/upload</div>
                </div>
            </div>
        ";

        var mockHandler = CreateMockHandler(mockHtml);
        var httpClient = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var service = new AnnaArchiveService(httpClient);

        // Act
        var results = await service.SearchAsync("test", limit: 10);

        // Assert
        var book = results.First();
        book.Title.Should().Be("Complete Metadata Book");
        book.Authors.Should().Contain("John Doe");
        book.Authors.Should().Contain("Jane Smith");
        book.Publisher.Should().Be("Penguin Random House");
        book.PublicationYear.Should().Be(2022);
        book.Language.Should().Be("English");
        book.Format.Should().Be("EPUB");
        book.FileSize.Should().Be("3.7MB");
        book.BookType.Should().Contain("fiction");
        book.Source.Should().Be("/lgli/lgrs/upload");
    }
}
