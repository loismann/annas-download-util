using System.Net;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The playback client.
///
/// Two things here are easy to get wrong and expensive when wrong. Spotify answers
/// "nothing is playing" with a 204 and no body, which must read as idle rather than
/// as a failure — the browser polls this, so getting it wrong means a permanent
/// error banner on a perfectly healthy account. And "play this song" must send
/// URIs while "play this playlist" must send a context, because only the latter
/// lets the next track follow.
/// </summary>
public class SpotifyPlaybackTests
{
    [Fact]
    public async Task ListsDevicesThatCanActuallyBeUsed()
    {
        var (service, handler) = Build("""
            {"devices":[
              {"id":"d1","is_active":true,"is_restricted":false,"name":"Kitchen","type":"Speaker","volume_percent":40},
              {"id":"d2","is_active":false,"is_restricted":false,"name":"Paul's iPad","type":"Tablet","volume_percent":100}
            ]}
            """);

        var devices = await service.GetDevicesAsync();

        devices.Should().HaveCount(2);
        devices[0].Name.Should().Be("Kitchen");
        devices[0].IsActive.Should().BeTrue();
        handler.LastUrl.Should().EndWith("/me/player/devices");
    }

    [Fact]
    public async Task KeepsRestrictedDevicesButMarksThem()
    {
        // Filtering them out makes a speaker the user can see simply vanish. Kept and
        // flagged, the UI can explain why it is not offered.
        var (service, _) = Build("""
            {"devices":[
              {"id":"d1","is_active":false,"is_restricted":true,"name":"Car","type":"Automobile","volume_percent":null}
            ]}
            """);

        var devices = await service.GetDevicesAsync();

        devices.Should().ContainSingle().Which.IsRestricted.Should().BeTrue();
    }

    [Fact]
    public async Task DropsADeviceWithNoIdBecauseNothingCanTargetIt()
    {
        var (service, _) = Build("""
            {"devices":[{"id":null,"is_active":false,"is_restricted":true,"name":"Ghost","type":"Unknown","volume_percent":null}]}
            """);

        (await service.GetDevicesAsync()).Should().BeEmpty();
    }

    // ─── idle is not an error ────────────────────────────────────────────────

    [Fact]
    public async Task ReportsNothingPlayingRatherThanFailing()
    {
        var (service, _) = Build(body: "", status: HttpStatusCode.NoContent);

        var state = await service.GetPlaybackStateAsync();

        state.Should().BeNull("Spotify answers 204 when nothing is active anywhere");
    }

    [Fact]
    public async Task ReadsWhatIsPlayingIncludingProgress()
    {
        var (service, _) = Build("""
            {
              "device":{"id":"d1","is_active":true,"is_restricted":false,"name":"Kitchen","type":"Speaker","volume_percent":40},
              "is_playing":true,
              "progress_ms":84000,
              "item":{
                "id":"t1","name":"Mystery Train","uri":"spotify:track:t1","duration_ms":146000,
                "artists":[{"id":"a1","name":"Elvis Presley"}],
                "album":{"id":"al1","name":"Sun Sessions","images":[{"url":"http://art/large.jpg","height":640,"width":640}]},
                "external_urls":{"spotify":"http://open.spotify.com/track/t1"}
              }
            }
            """);

        var state = await service.GetPlaybackStateAsync();

        state!.IsPlaying.Should().BeTrue();
        state.ProgressMs.Should().Be(84000);
        state.Track!.Name.Should().Be("Mystery Train");
        state.Track.Artists.Should().Be("Elvis Presley");
        state.Device!.Name.Should().Be("Kitchen");
    }

    [Fact]
    public async Task TreatsAMissingProgressAsTheStartRatherThanCrashing()
    {
        var (service, _) = Build("""
            {"device":null,"is_playing":false,"progress_ms":null,"item":null}
            """);

        var state = await service.GetPlaybackStateAsync();

        state!.ProgressMs.Should().Be(0);
        state.Device.Should().BeNull();
        state.Track.Should().BeNull();
    }

    // ─── playing one song vs playing a playlist ──────────────────────────────

    [Fact]
    public async Task PlayingSpecificTracksSendsThoseUris()
    {
        var (service, handler) = Build("{}");

        await service.PlayAsync(new SpotifyPlayRequest(
            DeviceId: "d1", Uris: ["spotify:track:a", "spotify:track:b"]));

        handler.LastUrl.Should().Contain("device_id=d1");
        handler.LastBody.Should().Contain("uris");
        handler.LastBody.Should().Contain("spotify:track:a");
        handler.LastBody.Should().NotContain("context_uri");
    }

    [Fact]
    public async Task PlayingAPlaylistSendsAContextSoTheNextTrackFollows()
    {
        // The distinction that matters: with a bare URI list, playback stops at the
        // end of what was sent. With a context, it carries on through the playlist.
        var (service, handler) = Build("{}");

        await service.PlayAsync(new SpotifyPlayRequest(
            DeviceId: "d1", ContextUri: "spotify:playlist:p1", OffsetPosition: 4));

        handler.LastBody.Should().Contain("context_uri");
        handler.LastBody.Should().Contain("spotify:playlist:p1");
        handler.LastBody.Should().Contain("\"position\":4");
        handler.LastBody.Should().NotContain("uris");
    }

    [Fact]
    public async Task ExplicitTracksWinOverAContext()
    {
        // "Play this song" from inside a playlist view sends both. The song must win,
        // otherwise clicking track 30 starts the playlist from the top.
        var (service, handler) = Build("{}");

        await service.PlayAsync(new SpotifyPlayRequest(
            Uris: ["spotify:track:a"], ContextUri: "spotify:playlist:p1"));

        handler.LastBody.Should().Contain("spotify:track:a");
        handler.LastBody.Should().NotContain("context_uri");
    }

    [Fact]
    public async Task PlayingNothingInParticularResumes()
    {
        var (service, handler) = Build("{}");

        await service.PlayAsync(new SpotifyPlayRequest(DeviceId: "d1"));

        handler.LastBody.Should().NotContain("uris");
        handler.LastBody.Should().NotContain("context_uri");
        handler.LastBody.Should().Contain("position_ms");
    }

    [Fact]
    public async Task NeverSendsMoreUrisThanSpotifyAccepts()
    {
        var (service, handler) = Build("{}");
        var many = Enumerable.Range(0, 250).Select(i => $"spotify:track:{i}").ToList();

        await service.PlayAsync(new SpotifyPlayRequest(Uris: many));

        var sent = JsonSerializer.Deserialize<PlayBody>(
            handler.LastBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        sent.Uris.Should().HaveCount(100, "Spotify rejects an add of more than 100 URIs outright");
    }

    // ─── skipping and shuffle ────────────────────────────────────────────────

    [Fact]
    public async Task SkippingUsesThePostVerbSpotifyExpects()
    {
        // Spotify is inconsistent: play and pause are PUT, the skips are POST. Sending
        // PUT here returns a 404 that names nothing, so this pins the verb.
        var (service, handler) = Build("{}");

        await service.SkipNextAsync();

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastUrl.Should().EndWith("/me/player/next");
    }

    [Fact]
    public async Task SkippingBackwardsIsADifferentEndpointFromSkippingForwards()
    {
        var (service, handler) = Build("{}");

        await service.SkipPreviousAsync("d1");

        handler.LastUrl.Should().Contain("/me/player/previous");
        handler.LastUrl.Should().Contain("device_id=d1");
    }

    [Fact]
    public async Task ShuffleSendsItsStateInTheQueryStringWhereSpotifyReadsIt()
    {
        // Spotify ignores a request body on this route entirely, so a body-based
        // implementation returns success while changing nothing at all.
        var (service, handler) = Build("{}");

        await service.SetShuffleAsync(true);

        handler.LastUrl.Should().Contain("/me/player/shuffle");
        handler.LastUrl.Should().Contain("state=true");
        handler.LastMethod.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task TurningShuffleOffIsDistinguishableFromTurningItOn()
    {
        var (service, handler) = Build("{}");

        await service.SetShuffleAsync(false, "d1");

        handler.LastUrl.Should().Contain("state=false");
        handler.LastUrl.Should().Contain("device_id=d1");
    }

    [Fact]
    public async Task ReportsShuffleAsSpotifySeesItRatherThanAsWeLastSetIt()
    {
        // Shuffle can be toggled on the phone or the desktop app. Reading it back is
        // what keeps the button from claiming the opposite of what is happening.
        var (service, _) = Build("""
            {"device":null,"is_playing":true,"progress_ms":0,"item":null,"shuffle_state":true}
            """);

        (await service.GetPlaybackStateAsync())!.IsShuffling.Should().BeTrue();
    }

    [Fact]
    public async Task TreatsAMissingShuffleFlagAsOff()
    {
        var (service, _) = Build("""{"device":null,"is_playing":false,"progress_ms":0,"item":null}""");

        (await service.GetPlaybackStateAsync())!.IsShuffling.Should().BeFalse();
    }

    [Fact]
    public async Task TransferringPlaybackTargetsExactlyOneDevice()
    {
        var (service, handler) = Build("{}");

        await service.TransferPlaybackAsync("d2", play: true);

        handler.LastBody.Should().Contain("device_ids");
        handler.LastBody.Should().Contain("d2");
        handler.LastMethod.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task PausingCanNameTheDevice()
    {
        var (service, handler) = Build("{}");

        await service.PauseAsync("d1");

        handler.LastUrl.Should().Contain("/me/player/pause");
        handler.LastUrl.Should().Contain("device_id=d1");
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    private sealed record PlayBody(List<string> Uris);

    private static (SpotifyService, RecordingHandler) Build(
        string body = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(body, status);
        var client = new HttpClient(handler);
        return (new SpotifyService(client, new StubTokens()), handler);
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubTokens : ISpotifyAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult("token");
        public string GetConnectedSpotifyUserId() => "spotify-me";
        public Task RecordSuccessfulCallAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default) =>
            Task.CompletedTask;
        public Task RecordUnavailableAsync(string message, CancellationToken token = default) => Task.CompletedTask;
    }
}
