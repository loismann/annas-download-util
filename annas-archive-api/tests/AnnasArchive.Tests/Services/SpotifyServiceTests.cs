using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Services;
using FluentAssertions;

namespace AnnasArchive.Tests.Services;

public class SpotifyServiceTests
{
    [Fact]
    public async Task GetUserPlaylistsAsync_PaginatesAndMaps2026ItemSummaries()
    {
        var requestedUris = new List<Uri>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedUris.Add(request.RequestUri!);

            if (request.RequestUri!.Query.Contains("offset=50", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "items": [
                        {
                          "id": "followed",
                          "name": "Followed Metadata Only",
                          "images": null,
                          "external_urls": { "spotify": "https://open.spotify.com/playlist/followed" },
                          "snapshot_id": "snapshot-followed"
                        }
                      ],
                      "total": 2,
                      "next": null,
                      "offset": 50,
                      "limit": 50
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""
                {
                  "items": [
                    {
                      "id": "owned",
                      "name": "Owned",
                      "images": [{ "url": "https://image.example/owned.jpg", "height": 300, "width": 300 }],
                      "items": { "href": "https://api.spotify.com/v1/playlists/owned/items", "total": 17 },
                      "external_urls": { "spotify": "https://open.spotify.com/playlist/owned" },
                      "snapshot_id": "snapshot-owned"
                    }
                  ],
                  "total": 2,
                  "next": "https://api.spotify.com/v1/me/playlists?offset=50&limit=50",
                  "offset": 0,
                  "limit": 50
                }
                """));
        });

        var service = CreateService(handler);
        var playlists = await service.GetUserPlaylistsAsync();

        playlists.Should().HaveCount(2);
        playlists[0].TrackCount.Should().Be(17);
        playlists[0].ContentsAvailable.Should().BeTrue();
        playlists[0].SnapshotId.Should().Be("snapshot-owned");
        playlists[1].TrackCount.Should().Be(0);
        playlists[1].ContentsAvailable.Should().BeFalse();
        requestedUris.Count(uri => uri.Host == "api.spotify.com").Should().Be(2);
    }

    [Fact]
    public async Task SearchTracksAsync_ClampsLimitToDevelopmentModeMaximum()
    {
        Uri? searchUri = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            searchUri = request.RequestUri;
            return Task.FromResult(JsonResponse("""
                { "tracks": { "items": [], "total": 0 } }
                """));
        });

        var service = CreateService(handler);
        await service.SearchTracksAsync("delta blues", 99);

        searchUri.Should().NotBeNull();
        searchUri!.Query.Should().Contain("limit=10");
    }

    [Fact]
    public async Task PlaylistWrites_UseCurrent2026EndpointsAndBodies()
    {
        var spotifyRequests = new List<CapturedRequest>();
        var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            spotifyRequests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Content == null ? null : await request.Content.ReadAsStringAsync()));

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/me/playlists")
            {
                return JsonResponse(
                    """
                    {
                      "id": "created-playlist",
                      "name": "Created Playlist",
                      "external_urls": { "spotify": "https://open.spotify.com/playlist/created-playlist" }
                    }
                    """,
                    HttpStatusCode.Created);
            }

            return JsonResponse("{}");
        });

        var service = CreateService(handler);
        await service.CreatePlaylistAsync("Created Playlist", "Description", false);
        await service.AddTracksToPlaylistAsync("created-playlist", ["spotify:track:one"]);
        await service.RemoveTracksFromPlaylistAsync("created-playlist", ["spotify:track:one"]);
        await service.RemovePlaylistsFromLibraryAsync(["spotify:playlist:created-playlist"]);

        spotifyRequests.Should().HaveCount(4);
        spotifyRequests[0].Uri.AbsolutePath.Should().Be("/v1/me/playlists");
        spotifyRequests[1].Uri.AbsolutePath.Should().Be("/v1/playlists/created-playlist/items");
        spotifyRequests[2].Uri.AbsolutePath.Should().Be("/v1/playlists/created-playlist/items");
        spotifyRequests[3].Uri.AbsolutePath.Should().Be("/v1/me/library");
        spotifyRequests[3].Uri.Query.Should().Contain("spotify%3Aplaylist%3Acreated-playlist");

        using var addBody = JsonDocument.Parse(spotifyRequests[1].Body!);
        addBody.RootElement.GetProperty("uris")[0].GetString().Should().Be("spotify:track:one");

        using var removeBody = JsonDocument.Parse(spotifyRequests[2].Body!);
        removeBody.RootElement.GetProperty("items")[0].GetProperty("uri").GetString()
            .Should().Be("spotify:track:one");
    }

    [Fact]
    public async Task SpotifyFailure_PreservesStatusReasonAndRetryAfter()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var response = JsonResponse(
                """
                {
                  "error": {
                    "status": 429,
                    "message": "Too many requests",
                    "reason": "QUOTA_EXCEEDED"
                  }
                }
                """,
                HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });

        var service = CreateService(handler);
        var act = () => service.GetUserPlaylistsAsync();

        var exception = await act.Should().ThrowAsync<SpotifyApiException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        exception.Which.Reason.Should().Be("QUOTA_EXCEEDED");
        exception.Which.IsQuotaExceeded.Should().BeTrue();
        exception.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    private static SpotifyService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new SpotifyService(client, new StubAccessTokenProvider());
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, Interlocked.Increment(ref _callCount));
    }

    private sealed class StubAccessTokenProvider : ISpotifyAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult("test-access-token");

        public Task RecordSuccessfulCallAsync(CancellationToken token = default) => Task.CompletedTask;

        public Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default) =>
            Task.CompletedTask;

        public Task RecordUnavailableAsync(string message, CancellationToken token = default) =>
            Task.CompletedTask;
    }
}
