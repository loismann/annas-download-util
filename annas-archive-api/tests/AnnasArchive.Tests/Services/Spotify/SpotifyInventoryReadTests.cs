using System.Net;
using System.Text;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Ownership mapping, single-playlist reads, and the recent-history approximation.
/// </summary>
public class SpotifyInventoryReadTests
{
    // ─── ownership ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MarksAPlaylistAsYoursWhenTheOwnerIdMatchesTheConnectedAccount()
    {
        var service = ServiceReturning(HttpStatusCode.OK, PlaylistsPayload(ownerId: "me"), connectedUserId: "me");

        var playlists = await service.GetUserPlaylistsAsync();

        playlists[0].IsOwnedByUser.Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotMarkSomeoneElsesPlaylistAsYours()
    {
        var service = ServiceReturning(HttpStatusCode.OK, PlaylistsPayload(ownerId: "someone-else"), connectedUserId: "me");

        var playlists = await service.GetUserPlaylistsAsync();

        playlists[0].IsOwnedByUser.Should().BeFalse();
        playlists[0].OwnerId.Should().Be("someone-else");
    }

    [Fact]
    public async Task ComparesOwnerIdsExactlyRatherThanIgnoringCase()
    {
        // Spotify user IDs are case-sensitive; "Me" is a different account to "me".
        var service = ServiceReturning(HttpStatusCode.OK, PlaylistsPayload(ownerId: "ME"), connectedUserId: "me");

        (await service.GetUserPlaylistsAsync())[0].IsOwnedByUser.Should().BeFalse();
    }

    [Fact]
    public async Task StillListsPlaylistsWhenTheConnectionCannotBeRead()
    {
        // Ownership is a display hint; Spotify enforces real access itself. Losing
        // it must degrade the labelling, not fail the whole listing.
        var service = ServiceReturning(
            HttpStatusCode.OK, PlaylistsPayload(ownerId: "me"), connectedUserId: null);

        var playlists = await service.GetUserPlaylistsAsync();

        playlists.Should().HaveCount(1);
        playlists[0].IsOwnedByUser.Should().BeFalse();
    }

    [Fact]
    public async Task ResolvesTheConnectedAccountOncePerListingNotOncePerPlaylist()
    {
        // Each read is a SQLite hit plus an AES decrypt. Doing it per playlist meant
        // 100+ decrypts for one listing on a real library.
        var tokens = new CountingTokens("me");
        var service = ServiceWith(tokens, HttpStatusCode.OK, ManyPlaylistsPayload(count: 25));

        await service.GetUserPlaylistsAsync();

        tokens.ConnectedUserIdCalls.Should().Be(1);
    }

    // ─── single playlist ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadsOnePlaylistById()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "id": "p1", "name": "Road Trip", "collaborative": false, "public": true,
              "items": { "href": "x", "total": 42 },
              "owner": { "id": "me", "display_name": "Paul" },
              "snapshot_id": "snap", "uri": "spotify:playlist:p1"
            }
            """, connectedUserId: "me");

        var playlist = await service.GetPlaylistAsync("p1");

        playlist!.Name.Should().Be("Road Trip");
        playlist.TrackCount.Should().Be(42);
        playlist.IsOwnedByUser.Should().BeTrue();
        playlist.IsPublic.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DoesNotCallSpotifyForABlankPlaylistId(string playlistId)
    {
        var calls = 0;
        var service = ServiceReturning(HttpStatusCode.OK, "{}", onRequest: () => calls++);

        (await service.GetPlaylistAsync(playlistId)).Should().BeNull();
        calls.Should().Be(0);
    }

    // ─── recent playlist contexts ────────────────────────────────────────────

    [Fact]
    public async Task CountsHowOftenEachPlaylistAppearsInRecentHistory()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "items": [
                { "played_at": "2026-08-01T10:00:00Z", "context": { "type": "playlist", "uri": "spotify:playlist:a" } },
                { "played_at": "2026-08-01T10:05:00Z", "context": { "type": "playlist", "uri": "spotify:playlist:a" } },
                { "played_at": "2026-08-01T10:10:00Z", "context": { "type": "playlist", "uri": "spotify:playlist:b" } }
              ]
            }
            """);

        var contexts = await service.GetRecentPlaylistContextsAsync();

        contexts.Should().HaveCount(2);
        contexts[0].PlaylistId.Should().Be("a");
        contexts[0].ObservedPlays.Should().Be(2);
        contexts[1].ObservedPlays.Should().Be(1);
    }

    [Fact]
    public async Task IgnoresPlaysWithNoContextOrANonPlaylistContext()
    {
        // An album or artist play is not evidence about any playlist, and a null
        // context is not evidence about anything.
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "items": [
                { "played_at": "2026-08-01T10:00:00Z", "context": null },
                { "played_at": "2026-08-01T10:01:00Z", "context": { "type": "album", "uri": "spotify:album:x" } },
                { "played_at": "2026-08-01T10:02:00Z", "context": { "type": "artist", "uri": "spotify:artist:y" } },
                { "played_at": "2026-08-01T10:03:00Z", "context": { "type": "playlist", "uri": "spotify:playlist:a" } }
              ]
            }
            """);

        var contexts = await service.GetRecentPlaylistContextsAsync();

        contexts.Should().HaveCount(1);
        contexts[0].PlaylistId.Should().Be("a");
    }

    [Fact]
    public async Task IgnoresAPlaylistContextWithAMalformedUri()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "items": [
                { "played_at": "2026-08-01T10:00:00Z", "context": { "type": "playlist", "uri": "spotify:playlist:" } },
                { "played_at": "2026-08-01T10:01:00Z", "context": { "type": "playlist", "uri": "garbage" } },
                { "played_at": "2026-08-01T10:02:00Z", "context": { "type": "playlist", "uri": null } }
              ]
            }
            """);

        (await service.GetRecentPlaylistContextsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsEmptyWhenSpotifyReportsNoRecentHistory()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """{ "items": [] }""");

        (await service.GetRecentPlaylistContextsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsEmptyWhenTheHistoryPayloadHasNoItemsAtAll()
    {
        var service = ServiceReturning(HttpStatusCode.OK, "{}");

        (await service.GetRecentPlaylistContextsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task OrdersMostObservedFirst()
    {
        var service = ServiceReturning(HttpStatusCode.OK, """
            {
              "items": [
                { "context": { "type": "playlist", "uri": "spotify:playlist:rare" } },
                { "context": { "type": "playlist", "uri": "spotify:playlist:common" } },
                { "context": { "type": "playlist", "uri": "spotify:playlist:common" } },
                { "context": { "type": "playlist", "uri": "spotify:playlist:common" } }
              ]
            }
            """);

        (await service.GetRecentPlaylistContextsAsync())[0].PlaylistId.Should().Be("common");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string PlaylistsPayload(string ownerId) => $$"""
        {
          "items": [
            {
              "id": "p1", "name": "Road Trip", "collaborative": false, "public": true,
              "items": { "href": "x", "total": 5 },
              "owner": { "id": "{{ownerId}}", "display_name": "Someone" },
              "snapshot_id": "snap", "uri": "spotify:playlist:p1"
            }
          ],
          "total": 1, "next": null, "offset": 0, "limit": 50
        }
        """;

    private static string ManyPlaylistsPayload(int count)
    {
        var items = string.Join(",", Enumerable.Range(0, count).Select(i => $$"""
            { "id": "p{{i}}", "name": "Playlist {{i}}", "collaborative": false,
              "items": { "href": "x", "total": 1 }, "owner": { "id": "me" } }
            """));

        return $$"""{ "items": [{{items}}], "total": {{count}}, "next": null, "offset": 0, "limit": 50 }""";
    }

    private static ISpotifyService ServiceReturning(
        HttpStatusCode status,
        string json,
        string? connectedUserId = "me",
        Action? onRequest = null) =>
        ServiceWith(new CountingTokens(connectedUserId), status, json, onRequest);

    private static ISpotifyService ServiceWith(
        ISpotifyAccessTokenProvider tokens, HttpStatusCode status, string json, Action? onRequest = null)
    {
        var handler = new StubHandler(_ =>
        {
            onRequest?.Invoke();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        return new SpotifyService(new HttpClient(handler), tokens);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CountingTokens(string? connectedUserId) : ISpotifyAccessTokenProvider
    {
        public int ConnectedUserIdCalls { get; private set; }

        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult("token");

        public string GetConnectedSpotifyUserId()
        {
            ConnectedUserIdCalls++;
            return connectedUserId
                ?? throw new SpotifyConnectionException("Spotify is not connected.", "Disconnected");
        }

        public Task RecordSuccessfulCallAsync(CancellationToken token = default) => Task.CompletedTask;

        public Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default) =>
            Task.CompletedTask;

        public Task RecordUnavailableAsync(string message, CancellationToken token = default) =>
            Task.CompletedTask;
    }
}
