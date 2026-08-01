using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Covers <see cref="IGoogleBooksService.SearchVolumesAsync"/>, added so
/// AudiobookEnrichmentService could stop hand-rolling the same HTTP call. The
/// null-vs-empty distinction is load-bearing: that caller feeds it straight into
/// rate-limit tracking.
/// </summary>
public class GoogleBooksSearchVolumesTests
{
    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly GoogleBooksService _service;

    public GoogleBooksSearchVolumesTests()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("GoogleBooks"))
               .Returns(new HttpClient(_handler.Object) { BaseAddress = new Uri("https://www.googleapis.com/") });

        _service = new GoogleBooksService(factory.Object, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task ReturnsEveryVolumeWithItsTitleAndAuthors()
    {
        Respond(HttpStatusCode.OK, new
        {
            items = new object[]
            {
                new { volumeInfo = new { title = "Dune", authors = new[] { "Frank Herbert" } } },
                new { volumeInfo = new { title = "Dune Messiah", authors = new[] { "Frank Herbert" } } }
            }
        });

        var volumes = await _service.SearchVolumesAsync("dune");

        volumes.Should().NotBeNull().And.HaveCount(2);
        volumes![0].Title.Should().Be("Dune");
        volumes[0].Authors.Should().BeEquivalentTo(new[] { "Frank Herbert" });
    }

    [Theory]
    [InlineData("1965", 1965)]
    [InlineData("1965-06", 1965)]
    [InlineData("1965-06-01", 1965)]
    public async Task ParsesTheYearFromEveryPublishedDateShapeGoogleReturns(string publishedDate, int expected)
    {
        Respond(HttpStatusCode.OK, new
        {
            items = new object[] { new { volumeInfo = new { title = "Dune", publishedDate } } }
        });

        var volumes = await _service.SearchVolumesAsync("dune");

        volumes![0].Year.Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    public async Task LeavesTheYearNullWhenPublishedDateIsUnparseable(string publishedDate)
    {
        Respond(HttpStatusCode.OK, new
        {
            items = new object[] { new { volumeInfo = new { title = "Dune", publishedDate } } }
        });

        var volumes = await _service.SearchVolumesAsync("dune");

        volumes![0].Year.Should().BeNull();
    }

    [Fact]
    public async Task PrefersThumbnailButFallsBackToSmallThumbnail()
    {
        Respond(HttpStatusCode.OK, new
        {
            items = new object[]
            {
                new { volumeInfo = new { title = "A", imageLinks = new { thumbnail = "http://x/big.jpg", smallThumbnail = "http://x/small.jpg" } } },
                new { volumeInfo = new { title = "B", imageLinks = new { smallThumbnail = "http://x/small.jpg" } } }
            }
        });

        var volumes = await _service.SearchVolumesAsync("q");

        volumes![0].ThumbnailUrl.Should().Be("https://x/big.jpg");
        volumes[1].ThumbnailUrl.Should().Be("https://x/small.jpg");
    }

    [Fact]
    public async Task UpgradesThumbnailsToHttps()
    {
        // Google still hands back http:// links; a mixed-content page silently drops them.
        Respond(HttpStatusCode.OK, new
        {
            items = new object[] { new { volumeInfo = new { title = "A", imageLinks = new { thumbnail = "http://books.google.com/x.jpg" } } } }
        });

        var volumes = await _service.SearchVolumesAsync("q");

        volumes![0].ThumbnailUrl.Should().StartWith("https://");
    }

    [Fact]
    public async Task DefaultsMissingFieldsRatherThanSkippingTheVolume()
    {
        Respond(HttpStatusCode.OK, new { items = new object[] { new { volumeInfo = new { } } } });

        var volumes = await _service.SearchVolumesAsync("q");

        volumes.Should().HaveCount(1);
        volumes![0].Title.Should().BeNull();
        volumes[0].Authors.Should().BeEmpty();
        volumes[0].Year.Should().BeNull();
        volumes[0].ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public async Task SkipsEntriesWithNoVolumeInfoAtAll()
    {
        Respond(HttpStatusCode.OK, new
        {
            items = new object[] { new { kind = "books#volume" }, new { volumeInfo = new { title = "Dune" } } }
        });

        var volumes = await _service.SearchVolumesAsync("q");

        volumes.Should().HaveCount(1);
        volumes![0].Title.Should().Be("Dune");
    }

    // ── null vs empty ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReturnsEmptyWhenTheApiAnswersWithNoItems()
    {
        Respond(HttpStatusCode.OK, new { totalItems = 0 });

        var volumes = await _service.SearchVolumesAsync("nothing matches this");

        volumes.Should().NotBeNull().And.BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReturnsNullWhenTheRequestItselfFails(HttpStatusCode status)
    {
        // Not an empty list: AudiobookEnrichmentService counts this against its rate
        // limiter, and "no results" must not trip it.
        Respond(status, new { });

        var volumes = await _service.SearchVolumesAsync("dune");

        volumes.Should().BeNull();
    }

    [Fact]
    public async Task ReturnsNullWhenTheRequestThrows()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));

        var volumes = await _service.SearchVolumesAsync("dune");

        volumes.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReturnsEmptyForABlankQueryWithoutCallingTheApi(string query)
    {
        var volumes = await _service.SearchVolumesAsync(query);

        volumes.Should().NotBeNull().And.BeEmpty();
        _handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    private void Respond(HttpStatusCode status, object body) =>
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body))
            });
}
