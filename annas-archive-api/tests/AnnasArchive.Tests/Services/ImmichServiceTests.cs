using System.Net;
using System.Text;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services.PhotoPrint;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Pins the Immich wire contract. Every payload below is the real shape returned
/// by the live instance (Immich 3.1), not an invention from the docs — the docs
/// omit which fields are nullable and that <c>nextPage</c> arrives as a string.
/// </summary>
public sealed class ImmichServiceTests
{
    /// <summary>Verbatim from POST /api/search/metadata, trimmed to two assets.</summary>
    private const string SearchJson = """
        {
          "albums": { "total": 0, "count": 0, "items": [], "facets": [] },
          "assets": {
            "total": 325,
            "count": 2,
            "nextPage": "2",
            "facets": [],
            "items": [
              {
                "id": "934045dd-3176-4f66-9f47-13747dd46602",
                "type": "IMAGE",
                "originalFileName": "IMG_4758.JPG",
                "originalMimeType": "image/jpeg",
                "fileCreatedAt": "2026-08-02T22:18:36.000Z",
                "localDateTime": "2026-08-02T22:18:36.000Z",
                "createdAt": "2026-08-03T00:22:19.379Z",
                "width": 1080,
                "height": 1620,
                "isFavorite": false,
                "isTrashed": false
              },
              {
                "id": "11111111-2222-3333-4444-555555555555",
                "type": "IMAGE",
                "originalFileName": "beach.jpg",
                "originalMimeType": "image/jpeg",
                "fileCreatedAt": "2026-07-04T18:00:00.000Z",
                "localDateTime": "2026-07-04T18:00:00.000Z",
                "createdAt": "2026-08-03T00:22:20.000Z",
                "width": 4032,
                "height": 3024,
                "isFavorite": true,
                "isTrashed": false
              }
            ]
          }
        }
        """;

    private static (ImmichService Service, Mock<HttpMessageHandler> Handler) Create(
        string? apiKey = "test-key",
        HttpResponseMessage? response = null,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
            {
                onRequest?.Invoke(request);
                return Task.FromResult(response ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SearchJson, Encoding.UTF8, "application/json")
                });
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Immich")).Returns(() =>
            new HttpClient(handler.Object, disposeHandler: false)
            {
                BaseAddress = new Uri("http://immich-server:2283/")
            });

        var config = Options.Create(new PhotoPrintConfiguration
        {
            Immich = new ImmichOptions
            {
                BaseUrl = "http://immich-server:2283",
                ApiKey = apiKey ?? string.Empty
            }
        });

        return (new ImmichService(factory.Object, config), handler);
    }

    // ─── Parsing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ParsesTheRealResponseShape()
    {
        var (service, _) = Create();

        var page = await service.SearchAsync(new ImmichSearchQuery());

        page.Total.Should().Be(325);
        page.Items.Should().HaveCount(2);
        page.NextPage.Should().Be(2, "Immich returns nextPage as a string page number, not a cursor");

        var first = page.Items[0];
        first.Id.Should().Be("934045dd-3176-4f66-9f47-13747dd46602");
        first.FileName.Should().Be("IMG_4758.JPG");
        first.Width.Should().Be(1080);
        first.Height.Should().Be(1620);
        first.IsFavorite.Should().BeFalse();
        page.Items[1].IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task TakenAt_UsesCaptureTime_NotUploadTime()
    {
        // The distinction that makes "photos from last week" work at all: a
        // Takeout backfill uploads decade-old photos today, and createdAt would
        // bunch every one of them under today's date.
        var (service, _) = Create();

        var page = await service.SearchAsync(new ImmichSearchQuery());

        page.Items[1].TakenAt.Should().Be(DateTimeOffset.Parse("2026-07-04T18:00:00.000Z"),
            "localDateTime is the capture moment; createdAt is 2026-08-03");
    }

    [Fact]
    public async Task Search_HandlesAnEmptyLibrary()
    {
        var empty = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"assets":{"total":0,"count":0,"nextPage":null,"items":[]}}""",
                Encoding.UTF8, "application/json")
        };
        var (service, _) = Create(response: empty);

        var page = await service.SearchAsync(new ImmichSearchQuery());

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
        page.NextPage.Should().BeNull("a null nextPage means there is no further page");
    }

    // ─── Request construction ────────────────────────────────────────────

    [Fact]
    public async Task Search_ExcludesVideosTrashedAndArchived()
    {
        string? body = null;
        var (service, _) = Create(onRequest: r => body = r.Content!.ReadAsStringAsync().Result);

        await service.SearchAsync(new ImmichSearchQuery());

        body.Should().Contain("\"type\":\"IMAGE\"", "a video cannot be printed");
        body.Should().Contain("\"isTrashed\":false", "a deleted photo must never reach the print picker");
        body.Should().Contain("\"isArchived\":false");
    }

    [Fact]
    public async Task Search_PassesDateRangeAndFavourites()
    {
        string? body = null;
        var (service, _) = Create(onRequest: r => body = r.Content!.ReadAsStringAsync().Result);

        await service.SearchAsync(new ImmichSearchQuery
        {
            TakenAfter = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            TakenBefore = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            FavoritesOnly = true
        });

        body.Should().Contain("takenAfter").And.Contain("2026-07-01");
        body.Should().Contain("takenBefore").And.Contain("2026-08-01");
        body.Should().Contain("\"isFavorite\":true");
    }

    [Fact]
    public async Task Search_ClampsPageSizeAndFloorsPageNumber()
    {
        string? body = null;
        var (service, _) = Create(onRequest: r => body = r.Content!.ReadAsStringAsync().Result);

        await service.SearchAsync(new ImmichSearchQuery { Size = 999_999, Page = 0 });

        body.Should().Contain("\"size\":1000").And.Contain("\"page\":1");
    }

    [Fact]
    public async Task FavouritesFilter_IsOmittedWhenNotRequested()
    {
        string? body = null;
        var (service, _) = Create(onRequest: r => body = r.Content!.ReadAsStringAsync().Result);

        await service.SearchAsync(new ImmichSearchQuery { FavoritesOnly = false });

        body.Should().NotContain("isFavorite",
            "sending isFavorite:false would hide favourites instead of including everything");
    }

    // ─── Binary fetches ──────────────────────────────────────────────────

    [Fact]
    public async Task OpenOriginal_RequestsTheOriginalRoute_AndStreamsBytes()
    {
        Uri? requested = null;
        var image = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
        };
        var (service, _) = Create(response: image, onRequest: r => requested = r.RequestUri);

        await using var stream = await service.OpenOriginalAsync("asset-1");
        var buffer = new byte[4];
        var read = await stream.ReadAsync(buffer);

        read.Should().Be(4);
        buffer[0].Should().Be(0xFF, "JPEG magic — the real bytes came through");
        requested!.AbsolutePath.Should().Be("/api/assets/asset-1/original");
    }

    [Fact]
    public async Task OpenThumbnail_AsksForThePreviewSize()
    {
        Uri? requested = null;
        var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
        var (service, _) = Create(response: image, onRequest: r => requested = r.RequestUri);

        await using var _stream = await service.OpenThumbnailAsync("asset-1");

        requested!.AbsolutePath.Should().Be("/api/assets/asset-1/thumbnail");
        requested.Query.Should().Contain("size=preview");
    }

    [Fact]
    public async Task MissingAsset_ThrowsATypedNotFound()
    {
        var (service, _) = Create(response: new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = async () => await service.OpenOriginalAsync("gone");

        await act.Should().ThrowAsync<ImmichAssetNotFoundException>()
            .WithMessage("*gone*");
    }

    [Fact]
    public async Task AssetIdsAreUrlEscaped()
    {
        Uri? requested = null;
        var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        var (service, _) = Create(response: image, onRequest: r => requested = r.RequestUri);

        await using var _stream = await service.OpenOriginalAsync("a b/../c");

        // The separators are what enable traversal, and they are encoded. A
        // literal ".." between %2F escapes is inert — it stays one path segment.
        requested!.AbsolutePath.Should().Be("/api/assets/a%20b%2F..%2Fc/original");
        requested.Segments.Should().HaveCount(5, "/ + api/ + assets/ + id/ + original");
        requested.Segments[3].Should().Be("a%20b%2F..%2Fc/", "the id stays one segment");
    }

    // ─── Configuration ───────────────────────────────────────────────────

    [Fact]
    public async Task WithoutAnApiKey_ItReportsUnconfiguredRatherThanCallingOut()
    {
        var (service, handler) = Create(apiKey: "");

        service.IsConfigured.Should().BeFalse();
        (await service.IsReachableAsync()).Should().BeFalse();

        var act = async () => await service.SearchAsync(new ImmichSearchQuery());
        await act.Should().ThrowAsync<InvalidOperationException>();

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task UnreachableImmich_ReportsFalseRatherThanThrowing()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Immich")).Returns(() =>
            new HttpClient(handler.Object, false) { BaseAddress = new Uri("http://immich-server:2283/") });

        var service = new ImmichService(factory.Object, Options.Create(new PhotoPrintConfiguration
        {
            Immich = new ImmichOptions { BaseUrl = "http://immich-server:2283", ApiKey = "k" }
        }));

        (await service.IsReachableAsync()).Should().BeFalse(
            "the library page must still render when Immich is down");
    }
}
