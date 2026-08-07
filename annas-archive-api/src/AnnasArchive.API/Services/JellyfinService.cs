using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>Just enough of Jellyfin's file-download response for the endpoint
/// layer to relay it back to the browser as a real download (Content-Type,
/// a filename for Content-Disposition, and length for a progress bar).</summary>
public record JellyfinDownloadResult(Stream Body, string ContentType, string? FileName, long? ContentLength);

/// <summary>Result of proxying a range-capable per-user stream request through
/// to Jellyfin — same shape as AudiobookshelfStreamResult, carrying just
/// enough of the upstream response for the endpoint layer to relay it verbatim
/// (including a 206 Partial Content status when Jellyfin returns one).</summary>
public record JellyfinStreamResult(Stream Body, string ContentType, string? ContentRange, long? ContentLength, int StatusCode);

/// <summary>One embedded audio or subtitle stream inside the file, as Jellyfin's
/// ffprobe-derived MediaStreams metadata describes it — Index is what the
/// Subtitles/Stream.vtt proxy endpoint needs to pick the right one back out.</summary>
public record JellyfinMediaTrack(int Index, string? Language, string? Title, bool IsDefault);

/// <summary>Resume state for our own <video> player — seconds, not the
/// 100ns "ticks" Jellyfin's API speaks natively, converted at the service
/// boundary so nothing above this layer needs to know about ticks. Track
/// lists describe what's embedded in the file so the frontend can offer a
/// picker; MediaSourceId is needed alongside a subtitle's Index to fetch it.
/// PlaybackMode is "direct" when the source file's container/codecs are
/// something a plain HTML5 <video> can decode natively (mp4/h264/aac|mp3) —
/// "transcode" means the frontend should use the HLS master-playlist route
/// instead (see GetMovieHlsMasterAsync), since e.g. an AVI container or AC3
/// audio has no browser-native decoder at all.</summary>
public record JellyfinPlaybackState(
    string ItemId,
    double ResumePositionSeconds,
    double? DurationSeconds,
    string? MediaSourceId,
    List<JellyfinMediaTrack> AudioTracks,
    List<JellyfinMediaTrack> SubtitleTracks,
    string PlaybackMode);

/// <summary>Jellyfin's raw HLS master playlist text plus the itemId the endpoint
/// layer needs in order to rewrite its relative URLs into this app's own HLS
/// proxy routes (Jellyfin's own URLs aren't reachable from the browser directly).</summary>
public record JellyfinHlsPlaylistResult(string ItemId, string PlaylistText);

public interface IJellyfinService
{
    /// <summary>Resolves a Sonarr/Radarr-identified show/movie to Jellyfin's own
    /// internal item, then builds a deep-link URL into Jellyfin's web player
    /// (routed through the CSP-stripping proxy — see JellyfinProxyBaseUrl —
    /// so it can actually be embedded in an iframe). Returns null if Jellyfin
    /// hasn't scanned/matched that item yet. Only used as a fallback for
    /// household members without personal credentials configured — see
    /// HasPersonalCredentials.</summary>
    Task<string?> GetTvEmbedUrlAsync(int tvdbId, int season, int episode, CancellationToken ct = default);

    Task<string?> GetMovieEmbedUrlAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>Proxies the movie's file down from Jellyfin's /Download endpoint —
    /// requires the configured API key to carry Jellyfin's "Allow media
    /// downloading" permission (unverified against the deployed instance; a 403
    /// here means that's off). Returns null if Jellyfin hasn't matched the movie.</summary>
    Task<JellyfinDownloadResult?> DownloadMovieAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>Same as DownloadMovieAsync, for a single episode.</summary>
    Task<JellyfinDownloadResult?> DownloadEpisodeAsync(int tvdbId, int season, int episode, CancellationToken ct = default);

    /// <summary>True if this household member (Paul/Mom/Dad) has a personal
    /// Jellyfin username+password configured — gates whether watch endpoints
    /// return the new native-player flow or fall back to the old embed.</summary>
    bool HasPersonalCredentials(string ownerName);

    Task<JellyfinPlaybackState?> GetMoviePlaybackStateAsync(string ownerName, int tmdbId, CancellationToken ct = default);

    Task<JellyfinPlaybackState?> GetEpisodePlaybackStateAsync(string ownerName, int tvdbId, int season, int episode, CancellationToken ct = default);

    /// <summary>Proxies the movie's raw stream from Jellyfin using this specific
    /// person's own Jellyfin token (so their own library/parental permissions are
    /// enforced), forwarding the incoming Range header so seeking works.</summary>
    Task<JellyfinStreamResult?> StreamMovieAsync(string ownerName, int tmdbId, string? rangeHeader, CancellationToken ct = default);

    Task<JellyfinStreamResult?> StreamEpisodeAsync(string ownerName, int tvdbId, int season, int episode, string? rangeHeader, CancellationToken ct = default);

    /// <summary>Returns Jellyfin's HLS "master" playlist for this movie, forced to
    /// H.264 video + AAC audio so every browser can play it regardless of the
    /// source file's actual codec/container — only meant to be used when
    /// GetMoviePlaybackStateAsync's PlaybackMode came back "transcode". Every
    /// relative URL inside the playlist text is Jellyfin's own (unreachable from
    /// the browser); the endpoint layer rewrites them into this app's own HLS
    /// proxy route before handing the playlist to the frontend.</summary>
    Task<JellyfinHlsPlaylistResult?> GetMovieHlsMasterAsync(string ownerName, int tmdbId, CancellationToken ct = default);

    Task<JellyfinHlsPlaylistResult?> GetEpisodeHlsMasterAsync(string ownerName, int tvdbId, int season, int episode, CancellationToken ct = default);

    /// <summary>Forwards one HLS sub-resource — the second-level "main.m3u8"
    /// playlist, or a .ts media segment — straight through to Jellyfin. Auth rides
    /// along in the query string (an api_key Jellyfin itself embedded when the
    /// master playlist was built, since that request explicitly asked for it —
    /// see GetHlsMasterAsync), not a per-user header, because by this point the
    /// caller only has the opaque Jellyfin itemId, not a household member's
    /// identity.</summary>
    Task<JellyfinStreamResult?> ProxyHlsResourceAsync(string itemId, string subPath, string? queryString, string? rangeHeader, CancellationToken ct = default);

    /// <summary>Writes the resume position back to Jellyfin's own per-user UserData
    /// for this item, so it's a single source of truth shared with any other
    /// Jellyfin client this person might use directly.</summary>
    Task<bool> SaveMoviePositionAsync(string ownerName, int tmdbId, double positionSeconds, CancellationToken ct = default);

    Task<bool> SaveEpisodePositionAsync(string ownerName, int tvdbId, int season, int episode, double positionSeconds, CancellationToken ct = default);

    /// <summary>Converts one embedded subtitle stream to WebVTT via Jellyfin's own
    /// subtitle-conversion endpoint (browsers can't parse embedded SRT/ASS/PGS out
    /// of a container themselves — this is what makes a <track> element possible).
    /// Exact route unverified against the deployed Jellyfin version.</summary>
    Task<string?> GetMovieSubtitleVttAsync(string ownerName, int tmdbId, string mediaSourceId, int subtitleIndex, CancellationToken ct = default);

    Task<string?> GetEpisodeSubtitleVttAsync(string ownerName, int tvdbId, int season, int episode, string mediaSourceId, int subtitleIndex, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Jellyfin's REST API — same shape as SonarrService/
/// RadarrService, for catalog lookups and the shared-admin-key embed/download
/// paths. Personal per-user playback (see the interface members above) is a
/// second mode layered on top: household members with credentials configured
/// (Jellyfin:UserCredentials:{Name}:Username/Password) get their own
/// Jellyfin session, so streaming honors their own account's permissions and
/// resume position is Jellyfin's own native per-user UserData — not something
/// this app tracks separately. Unconfigured members keep the original
/// shared-key embedded-iframe experience (see HasPersonalCredentials).
/// </summary>
public class JellyfinService : IJellyfinService
{
    private record UserCredential(string? Username, string? Password);
    private record UserSession(string AccessToken, string UserId);

    // Jellyfin requires every request (including AuthenticateByName itself) to
    // identify a "client" via this header — it's not optional, a request
    // missing it gets rejected outright regardless of token validity.
    private const string ClientAuthHeader =
        "MediaBrowser Client=\"Ferrer Utils\", Device=\"Server\", DeviceId=\"ferrer-utils-server\", Version=\"1.0.0\"";

    private readonly HttpClient _http;
    private readonly HttpClient _userHttp;
    private readonly Dictionary<string, UserCredential> _userCredentials;
    private readonly string _proxyBaseUrl;
    private string? _cachedServerId;

    // MUST be static, not an instance field: JellyfinService is a typed HttpClient
    // (services.AddHttpClient<IJellyfinService, JellyfinService>), which ASP.NET
    // Core registers Transient by default — a fresh instance per DI resolution, so
    // per separate incoming HTTP request. An instance-field cache here silently
    // never hit: every request that needed a session (master.m3u8, progress saves,
    // subtitle fetches, ...) re-ran AuthenticateByName from scratch. That alone
    // wouldn't matter except that Jellyfin invalidates a device's PREVIOUS token the
    // moment a new login happens for that same DeviceId (confirmed directly against
    // the live server) — so every one of those "redundant" logins was silently
    // killing whatever token an in-flight HLS playback session was still using,
    // which is what actually caused every 401 chased through this session, not a
    // transcoder race. Static makes this a real, request-spanning cache again.
    private static readonly ConcurrentDictionary<string, UserSession> _userSessions = new(StringComparer.OrdinalIgnoreCase);

    // Jellyfin's HLS transcoder appears to race when two requests for the same
    // item arrive close together (observed live: a seek's first segment succeeds,
    // the very next one — fired moments later by the player — 401s even on retry,
    // while the identical request sent in isolation always succeeds). Serializing
    // our own outbound requests per itemId means Jellyfin never actually sees
    // overlapping requests for one item, even if the player fires them
    // concurrently. Static (not an instance field) because JellyfinService is a
    // typed HttpClient, and DI may hand out more than one instance across
    // requests — this has to be shared process-wide to actually serialize anything.
    // Refcounted: an entry disappears once the last request for that item is
    // done. The ConcurrentDictionary this replaces kept a SemaphoreSlim per
    // item id for the life of the process.
    private static readonly Helpers.KeyedLocks _hlsItemLocks = new();

    public JellyfinService(HttpClient http, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _http = http;
        _userHttp = httpClientFactory.CreateClient("JellyfinUser");
        var baseUrl = configuration["Jellyfin:BaseUrl"];
        var apiKey = configuration["Jellyfin:ApiKey"];
        _proxyBaseUrl = (configuration["Jellyfin:ProxyBaseUrl"] ?? "").TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _http.BaseAddress = new Uri(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Remove("X-Emby-Token");
            _http.DefaultRequestHeaders.Add("X-Emby-Token", apiKey);
        }

        _userCredentials = configuration.GetSection("Jellyfin:UserCredentials")
            .GetChildren()
            .ToDictionary(
                s => s.Key,
                s => new UserCredential(s["Username"], s["Password"]),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool HasPersonalCredentials(string ownerName) =>
        _userCredentials.TryGetValue(ownerName, out var cred) &&
        !string.IsNullOrWhiteSpace(cred.Username) && !string.IsNullOrWhiteSpace(cred.Password);

    public async Task<string?> GetTvEmbedUrlAsync(int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        if (episodeItemId is null) return null;

        var serverId = await GetServerIdAsync(ct);
        return BuildEmbedUrl(episodeItemId, serverId);
    }

    public async Task<string?> GetMovieEmbedUrlAsync(int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        if (movieId is null) return null;

        var serverId = await GetServerIdAsync(ct);
        return BuildEmbedUrl(movieId, serverId);
    }

    public async Task<JellyfinDownloadResult?> DownloadMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await FetchDownloadAsync(movieId, ct);
    }

    public async Task<JellyfinDownloadResult?> DownloadEpisodeAsync(int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await FetchDownloadAsync(episodeItemId, ct);
    }

    public async Task<JellyfinPlaybackState?> GetMoviePlaybackStateAsync(string ownerName, int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await GetPlaybackStateAsync(ownerName, movieId, ct);
    }

    public async Task<JellyfinPlaybackState?> GetEpisodePlaybackStateAsync(string ownerName, int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await GetPlaybackStateAsync(ownerName, episodeItemId, ct);
    }

    public async Task<JellyfinStreamResult?> StreamMovieAsync(string ownerName, int tmdbId, string? rangeHeader, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await StreamItemAsync(ownerName, movieId, rangeHeader, ct);
    }

    public async Task<JellyfinStreamResult?> StreamEpisodeAsync(string ownerName, int tvdbId, int season, int episode, string? rangeHeader, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await StreamItemAsync(ownerName, episodeItemId, rangeHeader, ct);
    }

    public async Task<JellyfinHlsPlaylistResult?> GetMovieHlsMasterAsync(string ownerName, int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await GetHlsMasterAsync(ownerName, movieId, ct);
    }

    public async Task<JellyfinHlsPlaylistResult?> GetEpisodeHlsMasterAsync(string ownerName, int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await GetHlsMasterAsync(ownerName, episodeItemId, ct);
    }

    public async Task<JellyfinStreamResult?> ProxyHlsResourceAsync(string itemId, string subPath, string? queryString, string? rangeHeader, CancellationToken ct = default)
    {
        var url = $"/Videos/{itemId}/{subPath}" + (string.IsNullOrEmpty(queryString) ? "" : $"?{queryString}");

        // See _hlsItemLocks — only the request/header-exchange phase needs
        // serializing against Jellyfin, not the (potentially slow, client-bound)
        // body transfer, so the lock is released as soon as we have a response.
        HttpResponseMessage response;

        // Scoped block, not a method-level `using`: the lock must be gone before
        // the body transfer below, which is client-bound and slow.
        using (await _hlsItemLocks.AcquireAsync(itemId, ct))
        {
            try
            {
                response = await SendHlsResourceRequestAsync(url, rangeHeader, ct);

                // Seeking ahead of what Jellyfin's ffmpeg job has already generated
                // makes it kill and restart the transcode at the new position — a
                // segment requested in that brief window can come back 401/500 even
                // though nothing is actually wrong. One short retry papers over that
                // race rather than surfacing a real player error for it.
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    await Task.Delay(500, ct);
                    response = await SendHlsResourceRequestAsync(url, rangeHeader, ct);
                }
            }
            catch (HttpRequestException)
            {
                await Task.Delay(500, ct);
                response = await SendHlsResourceRequestAsync(url, rangeHeader, ct);
            }
        }

        var body = await response.Content.ReadAsStreamAsync(ct);
        return new JellyfinStreamResult(
            body,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            response.Content.Headers.ContentRange?.ToString(),
            response.Content.Headers.ContentLength,
            (int)response.StatusCode);
    }

    private Task<HttpResponseMessage> SendHlsResourceRequestAsync(string url, string? rangeHeader, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", ClientAuthHeader);
        if (!string.IsNullOrEmpty(rangeHeader))
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        return _userHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<bool> SaveMoviePositionAsync(string ownerName, int tmdbId, double positionSeconds, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        if (movieId is null) return false;
        await SavePositionAsync(ownerName, movieId, positionSeconds, ct);
        return true;
    }

    public async Task<bool> SaveEpisodePositionAsync(string ownerName, int tvdbId, int season, int episode, double positionSeconds, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        if (episodeItemId is null) return false;
        await SavePositionAsync(ownerName, episodeItemId, positionSeconds, ct);
        return true;
    }

    public async Task<string?> GetMovieSubtitleVttAsync(string ownerName, int tmdbId, string mediaSourceId, int subtitleIndex, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await GetSubtitleVttAsync(ownerName, movieId, mediaSourceId, subtitleIndex, ct);
    }

    public async Task<string?> GetEpisodeSubtitleVttAsync(string ownerName, int tvdbId, int season, int episode, string mediaSourceId, int subtitleIndex, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await GetSubtitleVttAsync(ownerName, episodeItemId, mediaSourceId, subtitleIndex, ct);
    }

    private async Task<string?> GetSubtitleVttAsync(string ownerName, string itemId, string mediaSourceId, int subtitleIndex, CancellationToken ct)
    {
        var session = await AuthenticateUserAsync(ownerName, ct);
        if (session is null) return null;

        var response = await SendAsUserWithRetryAsync(
            ownerName, session,
            () => new HttpRequestMessage(HttpMethod.Get, $"/Videos/{itemId}/{mediaSourceId}/Subtitles/{subtitleIndex}/Stream.vtt"),
            ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            if (response is not null)
                Log.Warning("[Jellyfin] Subtitle fetch for {Owner} item {ItemId} index {Index} returned {Status}", ownerName, itemId, subtitleIndex, response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<JellyfinPlaybackState?> GetPlaybackStateAsync(string ownerName, string itemId, CancellationToken ct)
    {
        var session = await AuthenticateUserAsync(ownerName, ct);
        if (session is null) return null;

        var response = await SendAsUserWithRetryAsync(
            ownerName, session,
            () => new HttpRequestMessage(HttpMethod.Get, $"/Users/{session.UserId}/Items/{itemId}?fields=MediaStreams,MediaSources"),
            ct);
        if (response is null || !response.IsSuccessStatusCode) return null;

        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var runtimeTicks = (long?)doc?["RunTimeTicks"];
        var positionTicks = (long?)doc?["UserData"]?["PlaybackPositionTicks"] ?? 0;
        var primarySource = (doc?["MediaSources"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var mediaSourceId = primarySource?["Id"]?.ToString();
        var container = primarySource?["Container"]?.ToString();

        var streams = (doc?["MediaStreams"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var audioTracks = streams.Where(s => (string?)s["Type"] == "Audio").Select(ToTrack).ToList();
        var subtitleTracks = streams.Where(s => (string?)s["Type"] == "Subtitle").Select(ToTrack).ToList();

        var videoCodec = streams.FirstOrDefault(s => (string?)s["Type"] == "Video")?["Codec"]?.ToString();
        var defaultAudioCodec = (streams.FirstOrDefault(s => (string?)s["Type"] == "Audio" && (bool?)s["IsDefault"] == true)
            ?? streams.FirstOrDefault(s => (string?)s["Type"] == "Audio"))?["Codec"]?.ToString();
        var playbackMode = IsBrowserCompatible(container, videoCodec, defaultAudioCodec) ? "direct" : "transcode";

        return new JellyfinPlaybackState(
            itemId,
            TicksToSeconds(positionTicks),
            runtimeTicks is null ? null : TicksToSeconds(runtimeTicks.Value),
            mediaSourceId,
            audioTracks,
            subtitleTracks,
            playbackMode);
    }

    /// <summary>Conservative allowlist (not a denylist of known-bad combos) matching
    /// exactly what a plain HTML5 <video src> can decode without help across
    /// Chrome/Firefox/Safari — notably, Safari has zero MKV/AVI/WebM support, and no
    /// major browser ships an AC3/DTS decoder. Anything outside this falls back to
    /// server-side HLS transcoding (see GetHlsMasterAsync) rather than silently
    /// failing to play (blank video, or video-with-no-audio).</summary>
    private static bool IsBrowserCompatible(string? container, string? videoCodec, string? audioCodec) =>
        container?.ToLowerInvariant() is "mp4" or "m4v" or "mov"
        && videoCodec?.ToLowerInvariant() == "h264"
        && audioCodec?.ToLowerInvariant() is "aac" or "mp3";

    private static JellyfinMediaTrack ToTrack(JsonObject stream) => new(
        (int)(stream["Index"] ?? 0),
        stream["Language"]?.ToString(),
        stream["DisplayTitle"]?.ToString(),
        (bool?)stream["IsDefault"] ?? false);

    private async Task<JellyfinStreamResult?> StreamItemAsync(string ownerName, string itemId, string? rangeHeader, CancellationToken ct)
    {
        var session = await AuthenticateUserAsync(ownerName, ct);
        if (session is null) return null;

        var response = await SendAsUserWithRetryAsync(
            ownerName, session,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"/Videos/{itemId}/stream?Static=true");
                if (!string.IsNullOrEmpty(rangeHeader))
                    request.Headers.TryAddWithoutValidation("Range", rangeHeader);
                return request;
            },
            ct, HttpCompletionOption.ResponseHeadersRead);
        if (response is null) return null;

        var body = await response.Content.ReadAsStreamAsync(ct);
        return new JellyfinStreamResult(
            body,
            response.Content.Headers.ContentType?.ToString() ?? "video/mp4",
            response.Content.Headers.ContentRange?.ToString(),
            response.Content.Headers.ContentLength,
            (int)response.StatusCode);
    }

    private async Task<JellyfinHlsPlaylistResult?> GetHlsMasterAsync(string ownerName, string itemId, CancellationToken ct)
    {
        var session = await AuthenticateUserAsync(ownerName, ct);
        if (session is null) return null;

        // master.m3u8 rejects with 400 ("mediaSourceId field is required") without
        // this — unlike /Videos/{id}/stream, it won't infer the source on its own
        // even when there's only one.
        var sourceResponse = await SendAsUserWithRetryAsync(
            ownerName, session,
            () => new HttpRequestMessage(HttpMethod.Get, $"/Users/{session.UserId}/Items/{itemId}?fields=MediaSources"),
            ct);
        if (sourceResponse is null || !sourceResponse.IsSuccessStatusCode) return null;
        var sourceDoc = await sourceResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var mediaSourceId = (((sourceDoc?["MediaSources"] as JsonArray)?.FirstOrDefault() as JsonObject)?["Id"])?.ToString();
        if (mediaSourceId is null) return null;

        // Forcing H.264/AAC output (rather than leaving codec negotiation to
        // Jellyfin's defaults) means Jellyfin stream-copies whichever track is
        // already compatible and only transcodes the ones that aren't — an
        // already-H.264 file costs little extra CPU, just the audio gets
        // re-encoded. api_key rides in the query (not just a header)
        // specifically so Jellyfin bakes it into every relative URL it emits
        // for the segment/sub-playlist requests that follow — see
        // ProxyHlsResourceAsync, which has no other way to authenticate since
        // it only knows the opaque itemId, not which household member asked.
        //
        // PlaySessionId is how real Jellyfin clients tell the server that every
        // segment request across a seek belongs to the SAME ongoing playback —
        // without it, a seek to a not-yet-generated segment (a normal jump ahead
        // in a movie) can 401 because Jellyfin has no stable session to attach
        // the seek/re-encode to. Generated once per "watch" click and threaded
        // through every subsequent URL automatically by Jellyfin itself, the same
        // way it already echoes MediaSourceId back into every segment URL.
        var playSessionId = Guid.NewGuid().ToString("N");
        var query = $"MediaSourceId={Uri.EscapeDataString(mediaSourceId)}&VideoCodec=h264&AudioCodec=aac&TranscodingMaxAudioChannels=2&SegmentContainer=ts"
            + $"&PlaySessionId={playSessionId}&api_key={Uri.EscapeDataString(session.AccessToken)}&DeviceId=ferrer-utils-server";

        var response = await SendAsUserWithRetryAsync(
            ownerName, session,
            () => new HttpRequestMessage(HttpMethod.Get, $"/Videos/{itemId}/master.m3u8?{query}"),
            ct);
        if (response is null || !response.IsSuccessStatusCode) return null;

        var text = await response.Content.ReadAsStringAsync(ct);
        return new JellyfinHlsPlaylistResult(itemId, text);
    }

    /// <summary>Patches just the resume position onto Jellyfin's per-user UserData
    /// for this item — the exact request/response shape of this endpoint is
    /// unverified against the deployed Jellyfin version; if resume stops
    /// persisting, check this first (Log.Warning below will show a non-success
    /// status if Jellyfin rejects the body outright).</summary>
    private async Task SavePositionAsync(string ownerName, string itemId, double positionSeconds, CancellationToken ct)
    {
        var session = await AuthenticateUserAsync(ownerName, ct);
        if (session is null) return;

        var response = await SendAsUserWithRetryAsync(
            ownerName, session,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/Users/{session.UserId}/Items/{itemId}/UserData");
                request.Content = JsonContent.Create(new { PlaybackPositionTicks = SecondsToTicks(positionSeconds) });
                return request;
            },
            ct);

        if (response is not null && !response.IsSuccessStatusCode)
            Log.Warning("[Jellyfin] Saving resume position for {Owner} on item {ItemId} returned {Status}", ownerName, itemId, response.StatusCode);
    }

    private async Task<UserSession?> AuthenticateUserAsync(string ownerName, CancellationToken ct)
    {
        if (_userSessions.TryGetValue(ownerName, out var cached)) return cached;
        if (!_userCredentials.TryGetValue(ownerName, out var cred) || string.IsNullOrWhiteSpace(cred.Username) || string.IsNullOrWhiteSpace(cred.Password))
            return null;

        return await AuthenticateAndCacheAsync(ownerName, cred, ct);
    }

    private async Task<UserSession?> AuthenticateAndCacheAsync(string ownerName, UserCredential cred, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", ClientAuthHeader);
        request.Content = JsonContent.Create(new { Username = cred.Username, Pw = cred.Password });

        var response = await _userHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Jellyfin] Personal login failed for {Owner}: {Status}", ownerName, response.StatusCode);
            return null;
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var accessToken = doc?["AccessToken"]?.ToString();
        var userId = doc?["User"]?["Id"]?.ToString();
        if (accessToken is null || userId is null)
        {
            Log.Warning("[Jellyfin] Personal login for {Owner} succeeded but response was missing AccessToken/UserId", ownerName);
            return null;
        }

        var session = new UserSession(accessToken, userId);
        _userSessions[ownerName] = session;
        return session;
    }

    /// <summary>Sends a per-user request, retrying once against a freshly
    /// authenticated session if the cached token has gone stale (401). The
    /// request itself is rebuilt on retry since a sent HttpRequestMessage
    /// can't be reused.</summary>
    private async Task<HttpResponseMessage?> SendAsUserWithRetryAsync(
        string ownerName, UserSession session, Func<HttpRequestMessage> requestFactory, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var response = await SendAsUserAsync(session, requestFactory(), ct, completion);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        _userSessions.TryRemove(ownerName, out _);
        if (!_userCredentials.TryGetValue(ownerName, out var cred)) return response;

        var refreshed = await AuthenticateAndCacheAsync(ownerName, cred, ct);
        if (refreshed is null) return response;

        return await SendAsUserAsync(refreshed, requestFactory(), ct, completion);
    }

    private Task<HttpResponseMessage> SendAsUserAsync(UserSession session, HttpRequestMessage request, CancellationToken ct, HttpCompletionOption completion)
    {
        request.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", ClientAuthHeader);
        return _userHttp.SendAsync(request, completion, ct);
    }

    private static double TicksToSeconds(long ticks) => ticks / 10_000_000.0;
    private static long SecondsToTicks(double seconds) => (long)(seconds * 10_000_000);

    private async Task<string?> ResolveMovieItemIdAsync(int tmdbId, CancellationToken ct)
    {
        var movieId = await FindItemIdByProviderAsync("Movie", "hasTmdbId", "Tmdb", tmdbId.ToString(), ct);
        if (movieId is null)
            Log.Information("[Jellyfin] No movie found matching TMDB id {TmdbId}", tmdbId);
        return movieId;
    }

    private async Task<string?> ResolveEpisodeItemIdAsync(int tvdbId, int season, int episode, CancellationToken ct)
    {
        var seriesId = await FindItemIdByProviderAsync("Series", "hasTvdbId", "Tvdb", tvdbId.ToString(), ct);
        if (seriesId is null)
        {
            Log.Information("[Jellyfin] No series found matching TVDB id {TvdbId}", tvdbId);
            return null;
        }

        var episodesResponse = await _http.GetAsync($"/Shows/{seriesId}/Episodes?fields=ProviderIds", ct);
        episodesResponse.EnsureSuccessStatusCode();
        var episodesDoc = await episodesResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var items = episodesDoc?["Items"] as JsonArray ?? [];

        var episodeItem = items.FirstOrDefault(item =>
            item is JsonObject obj &&
            (int?)obj["ParentIndexNumber"] == season &&
            (int?)obj["IndexNumber"] == episode);

        var episodeItemId = (episodeItem as JsonObject)?["Id"]?.ToString();
        if (episodeItemId is null)
            Log.Information("[Jellyfin] Series {SeriesId} found but no matching S{Season}E{Episode}", seriesId, season, episode);

        return episodeItemId;
    }

    private async Task<JellyfinDownloadResult> FetchDownloadAsync(string itemId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/Items/{itemId}/Download", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        var body = await response.Content.ReadAsStreamAsync(ct);

        return new JellyfinDownloadResult(
            body,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            fileName?.Trim('"'),
            response.Content.Headers.ContentLength);
    }

    // Jellyfin has no "give me the item with exactly this external ID" query
    // param (confirmed against its source — only has-any-ID booleans exist),
    // so this fetches everything with *some* ID from that provider and
    // filters client-side for the exact match.
    private async Task<string?> FindItemIdByProviderAsync(
        string includeItemType, string hasIdFilter, string providerKey, string providerValue, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/Items?IncludeItemTypes={includeItemType}&Recursive=true&{hasIdFilter}=true&fields=ProviderIds", ct);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var items = doc?["Items"] as JsonArray ?? [];

        foreach (var item in items)
        {
            if (item is not JsonObject obj) continue;
            var providerIds = obj["ProviderIds"] as JsonObject;
            if (providerIds?[providerKey]?.ToString() == providerValue)
                return obj["Id"]?.ToString();
        }

        return null;
    }

    private async Task<string> GetServerIdAsync(CancellationToken ct)
    {
        if (_cachedServerId is not null) return _cachedServerId;

        var response = await _http.GetAsync("/System/Info/Public", ct);
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        _cachedServerId = doc?["Id"]?.ToString() ?? throw new InvalidOperationException("Jellyfin did not return a server Id.");
        return _cachedServerId;
    }

    private string BuildEmbedUrl(string itemId, string serverId) =>
        $"{_proxyBaseUrl}/web/index.html#!/details?id={itemId}&serverId={serverId}&context=home&autoplay=true";
}
