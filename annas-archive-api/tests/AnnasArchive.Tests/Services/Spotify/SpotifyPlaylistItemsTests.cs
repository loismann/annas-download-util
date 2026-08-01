using System.Net;
using System.Text;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Reading playlist contents against the 2026 shape. The awkward entries are the
/// point: episodes have no artists, local files have no catalog URI, and a null
/// <c>item</c> is what Spotify returns for content pulled from the catalog after
/// it was added. None of those may crash, and none may silently vanish.
/// </summary>
public class SpotifyPlaylistItemsTests
{
    [Fact]
    public async Task ReadsTracksWithTheirPositionArtistsAndAlbum()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "href": "https://api.spotify.com/v1/playlists/p/items",
              "limit": 50, "next": null, "offset": 0, "previous": null, "total": 2,
              "items": [
                {
                  "added_at": "2026-01-05T10:00:00Z", "is_local": false,
                  "item": {
                    "id": "t1", "name": "Mystery Train", "uri": "spotify:track:t1",
                    "type": "track", "duration_ms": 145000,
                    "artists": [{ "id": "a1", "name": "Elvis Presley" }],
                    "album": { "id": "al1", "name": "Sun Sessions", "images": [] },
                    "external_ids": { "isrc": "USRC17607839" },
                    "external_urls": { "spotify": "https://open.spotify.com/track/t1" }
                  }
                },
                {
                  "added_at": "2026-01-06T10:00:00Z", "is_local": false,
                  "item": {
                    "id": "t2", "name": "Cross Road Blues", "uri": "spotify:track:t2",
                    "type": "track", "duration_ms": 160000,
                    "artists": [{ "id": "a2", "name": "Robert Johnson" }, { "id": "a3", "name": "Guest" }],
                    "album": { "id": "al2", "name": "King of the Delta Blues", "images": [] },
                    "external_urls": { "spotify": "https://open.spotify.com/track/t2" }
                  }
                }
              ]
            }
            """);

        var page = await service.GetPlaylistItemsAsync("p");

        page.Access.Should().Be(SpotifyContentsAccess.Available);
        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items[0].Kind.Should().Be(SpotifyItemKind.Track);
        page.Items[0].Name.Should().Be("Mystery Train");
        page.Items[0].Artists.Should().Be("Elvis Presley");
        page.Items[0].AlbumName.Should().Be("Sun Sessions");
        page.Items[0].Position.Should().Be(0);
        page.Items[0].Isrc.Should().Be("USRC17607839");
        page.Items[1].Artists.Should().Be("Robert Johnson, Guest");
        page.Items[1].Position.Should().Be(1);
    }

    [Fact]
    public async Task NumbersPositionsFromTheRequestedOffsetNotFromZero()
    {
        // Otherwise page two reports items 1-50 all over again.
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "limit": 50, "next": null, "offset": 50, "total": 120,
              "items": [
                { "added_at": "2026-01-05T10:00:00Z", "is_local": false,
                  "item": { "id": "t51", "name": "Fifty First", "uri": "spotify:track:t51",
                            "type": "track", "duration_ms": 1000 } },
                { "added_at": "2026-01-05T10:00:00Z", "is_local": false,
                  "item": { "id": "t52", "name": "Fifty Second", "uri": "spotify:track:t52",
                            "type": "track", "duration_ms": 1000 } }
              ]
            }
            """);

        var page = await service.GetPlaylistItemsAsync("p", offset: 50);

        page.Items[0].Position.Should().Be(50);
        page.Items[1].Position.Should().Be(51);
    }

    [Fact]
    public async Task ReadsAPodcastEpisodeWithoutInventingAnArtist()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "limit": 50, "next": null, "offset": 0, "total": 1,
              "items": [
                {
                  "added_at": "2026-01-05T10:00:00Z", "is_local": false,
                  "item": {
                    "id": "e1", "name": "Episode 12", "uri": "spotify:episode:e1",
                    "type": "episode", "duration_ms": 2400000,
                    "external_urls": { "spotify": "https://open.spotify.com/episode/e1" }
                  }
                }
              ]
            }
            """);

        var page = await service.GetPlaylistItemsAsync("p");

        page.Items[0].Kind.Should().Be(SpotifyItemKind.Episode);
        page.Items[0].Artists.Should().BeEmpty();
        page.Items[0].AlbumName.Should().BeNull();
    }

    [Fact]
    public async Task MarksALocalFileAsLocalEvenThoughSpotifyTypesItAsATrack()
    {
        // Local files cannot be re-added through the API, so a later merge or
        // restore has to know this one is different.
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "limit": 50, "next": null, "offset": 0, "total": 1,
              "items": [
                {
                  "added_at": "2026-01-05T10:00:00Z", "is_local": true,
                  "item": {
                    "id": null, "name": "Home Recording", "uri": null,
                    "type": "track", "duration_ms": 100000, "is_local": true
                  }
                }
              ]
            }
            """);

        var page = await service.GetPlaylistItemsAsync("p");

        page.Items[0].Kind.Should().Be(SpotifyItemKind.Local);
        page.Items[0].IsLocal.Should().BeTrue();
        page.Items[0].Uri.Should().BeNull();
    }

    [Fact]
    public async Task KeepsAnEntryWhoseItemIsNullRatherThanDroppingIt()
    {
        // Dropping it would shift every later position and under-report the count.
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "limit": 50, "next": null, "offset": 0, "total": 2,
              "items": [
                { "added_at": "2026-01-05T10:00:00Z", "is_local": false, "item": null },
                {
                  "added_at": "2026-01-06T10:00:00Z", "is_local": false,
                  "item": { "id": "t2", "name": "Real Track", "uri": "spotify:track:t2",
                            "type": "track", "duration_ms": 1000 }
                }
              ]
            }
            """);

        var page = await service.GetPlaylistItemsAsync("p");

        page.Items.Should().HaveCount(2);
        page.Items[0].Kind.Should().Be(SpotifyItemKind.Unavailable);
        page.Items[0].Name.Should().BeNull();
        page.Items[1].Position.Should().Be(1);
    }

    // ─── access, not emptiness ───────────────────────────────────────────────

    [Fact]
    public async Task ReportsForbiddenRatherThanEmptyWhenSpotifyRefusesTheContents()
    {
        // The headline bug: a followed playlist full of music must never read as 0.
        var service = ServiceReturning(HttpStatusCode.Forbidden, """{ "error": { "status": 403 } }""");

        var page = await service.GetPlaylistItemsAsync("p");

        page.Access.Should().Be(SpotifyContentsAccess.Forbidden);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportsUnavailableWhenTheItemsCollectionIsMissingEntirely()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """{ "limit": 50, "offset": 0, "total": 0 }""");

        var page = await service.GetPlaylistItemsAsync("p");

        page.Access.Should().Be(SpotifyContentsAccess.Unavailable);
    }

    [Fact]
    public async Task ReportsAvailableWithZeroItemsForAGenuinelyEmptyPlaylist()
    {
        // The one case where zero is the truth, and it must stay distinguishable
        // from the two above.
        var service = ServiceReturning(HttpStatusCode.OK,
            """{ "limit": 50, "next": null, "offset": 0, "total": 0, "items": [] }""");

        var page = await service.GetPlaylistItemsAsync("p");

        page.Access.Should().Be(SpotifyContentsAccess.Available);
        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task RethrowsFailuresThatAreNotAnAccessDecision()
    {
        var service = ServiceReturning(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<SpotifyApiException>(() => service.GetPlaylistItemsAsync("p"));
    }

    // ─── paging ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlagsMoreToComeWhenSpotifyReturnsANextLink()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "limit": 50, "offset": 0, "total": 120,
              "next": "https://api.spotify.com/v1/playlists/p/items?offset=50&limit=50",
              "items": []
            }
            """);

        (await service.GetPlaylistItemsAsync("p")).HasMore.Should().BeTrue();
    }

    [Theory]
    [InlineData(50, 50)]    // Spotify's maximum, passed through
    [InlineData(10, 10)]    // a legal value, left alone
    [InlineData(999, 50)]   // above the maximum
    [InlineData(0, 1)]      // a page of nothing is meaningless; nearest legal value
    [InlineData(-5, 1)]     // below the minimum
    public async Task ClampsTheRequestedPageSizeToWhatSpotifyAccepts(int requested, int expected)
    {
        Uri? seen = null;
        var service = ServiceReturning(HttpStatusCode.OK, ItemsPayload(0, 0), request => seen = request.RequestUri);

        await service.GetPlaylistItemsAsync("p", offset: 0, limit: requested);

        seen!.Query.Should().Contain($"limit={expected}");
    }

    [Fact]
    public async Task NeverAsksForANegativeOffset()
    {
        Uri? seen = null;
        var service = ServiceReturning(HttpStatusCode.OK, ItemsPayload(0, 0), request => seen = request.RequestUri);

        await service.GetPlaylistItemsAsync("p", offset: -10);

        seen!.Query.Should().Contain("offset=0");
    }

    [Fact]
    public async Task EscapesThePlaylistIdInTheRequestPath()
    {
        Uri? seen = null;
        var service = ServiceReturning(HttpStatusCode.OK, ItemsPayload(0, 0), request => seen = request.RequestUri);

        await service.GetPlaylistItemsAsync("id with spaces");

        seen!.AbsoluteUri.Should().Contain("id%20with%20spaces");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string ItemsPayload(int offset, int total) =>
        $$"""{ "limit": 50, "next": null, "offset": {{offset}}, "total": {{total}}, "items": [] }""";

    private static ISpotifyService ServiceReturning(
        HttpStatusCode status, string json, Action<HttpRequestMessage>? inspect = null)
    {
        var handler = new StubHandler(request =>
        {
            inspect?.Invoke(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        return new SpotifyService(new HttpClient(handler), new StubTokens());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubTokens : ISpotifyAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult("token");

        public string GetConnectedSpotifyUserId() => "me";

        public Task RecordSuccessfulCallAsync(CancellationToken token = default) => Task.CompletedTask;

        public Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default) =>
            Task.CompletedTask;

        public Task RecordUnavailableAsync(string message, CancellationToken token = default) =>
            Task.CompletedTask;
    }
}
