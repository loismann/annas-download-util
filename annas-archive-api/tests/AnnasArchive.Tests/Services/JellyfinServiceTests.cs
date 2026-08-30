using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Moq.Protected;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The decisions <see cref="JellyfinService"/> makes on the way to the player.
///
/// <para>683 lines with no test file. Most of it is plumbing that only a real
/// Jellyfin can exercise, but three things in it are judgement rather than
/// plumbing, and all three fail silently when they are wrong: whether a file can
/// be played natively or has to be transcoded, where playback resumes from, and
/// what happens when a cached login has gone stale. A wrong answer to the first
/// is a blank video or video-with-no-audio; to the second, a movie that restarts
/// from zero; to the third, a household member who is simply logged out with no
/// explanation.</para>
///
/// <para>These drive the real service over a stubbed handler and assert those
/// three, plus the null-handling around them — Jellyfin's JSON is read with
/// <c>JsonObject</c> indexers throughout, so a missing field is a
/// <c>NullReferenceException</c> unless every read is guarded.</para>
/// </summary>
public class JellyfinServiceTests
{
    private const string Owner = "Dad";

    /// <summary>
    /// Session caching lives in a <c>static</c> dictionary on the class, so it
    /// outlives any one instance and would leak between tests. Every test gets a
    /// unique owner name rather than reaching in to clear it — the isolation is
    /// then a property of the test, not of a cleanup step someone can forget.
    /// </summary>
    private static string UniqueOwner([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        $"{caller}-{Guid.NewGuid():N}";

    /// <summary>Records every request and answers from a caller-supplied function.</summary>
    private sealed class Recorder
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Urls { get; } = [];

        public HttpMessageHandler Handler(Func<HttpRequestMessage, int, HttpResponseMessage> reply)
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                {
                    Requests.Add(req);
                    Urls.Add(req.RequestUri!.ToString());
                    return reply(req, Requests.Count - 1);
                });
            return mock.Object;
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string GoodLogin = """{"AccessToken":"tok-1","User":{"Id":"user-1"}}""";

    /// <summary>
    /// Builds the service against one stub handler. Both the shared client and the
    /// per-user client point at it, so a test can assert on the whole conversation.
    /// </summary>
    private static (JellyfinService Service, Recorder Rec) Build(
        Func<HttpRequestMessage, int, HttpResponseMessage> reply,
        string owner = Owner,
        string? username = "dad", string? password = "pw")
    {
        var rec = new Recorder();
        var handler = rec.Handler(reply);

        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://jellyfin.test") };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(Client);

        var settings = new Dictionary<string, string?>
        {
            ["Jellyfin:BaseUrl"] = "http://jellyfin.test",
            ["Jellyfin:ApiKey"] = "server-key",
            ["Jellyfin:ProxyBaseUrl"] = "http://proxy.test/",
            [$"Jellyfin:UserCredentials:{owner}:Username"] = username,
            [$"Jellyfin:UserCredentials:{owner}:Password"] = password,
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (new JellyfinService(Client(), factory.Object, config), rec);
    }

    /// <summary>
    /// One item document. Everything the playback-state reader looks at is a
    /// parameter, because the interesting cases are all "this field is missing or
    /// is a value nobody expected".
    /// </summary>
    private static string ItemJson(
        string container = "mp4",
        string videoCodec = "h264",
        string? defaultAudioCodec = "aac",
        string? secondAudioCodec = null,
        long? positionTicks = 0,
        long? runTimeTicks = 72_000_000_000)
    {
        var streams = new List<string>
        {
            "{\"Type\":\"Video\",\"Index\":0,\"Codec\":\"" + videoCodec + "\"}"
        };

        if (secondAudioCodec is not null)
            streams.Add("{\"Type\":\"Audio\",\"Index\":1,\"Codec\":\"" + secondAudioCodec
                + "\",\"IsDefault\":false,\"Language\":\"eng\"}");

        if (defaultAudioCodec is not null)
            streams.Add("{\"Type\":\"Audio\",\"Index\":2,\"Codec\":\"" + defaultAudioCodec
                + "\",\"IsDefault\":true,\"Language\":\"eng\",\"DisplayTitle\":\"English\"}");

        streams.Add("{\"Type\":\"Subtitle\",\"Index\":3,\"Language\":\"spa\","
            + "\"DisplayTitle\":\"Spanish\",\"IsDefault\":false}");

        var userData = positionTicks is null
            ? "" : "\"UserData\":{\"PlaybackPositionTicks\":" + positionTicks + "},";
        var runTime = runTimeTicks is null
            ? "" : "\"RunTimeTicks\":" + runTimeTicks + ",";

        return "{" + runTime + userData
            + "\"MediaSources\":[{\"Id\":\"src-9\",\"Container\":\"" + container + "\"}],"
            + "\"MediaStreams\":[" + string.Join(",", streams) + "]}";
    }

    /// <summary>
    /// Reaching a playback state takes three round trips, and the tests below care
    /// about only the last one.
    ///
    /// <para><c>GET /Items</c> resolves the TMDB id to Jellyfin's own item id using
    /// the <i>server</i> key; <c>POST /Users/AuthenticateByName</c> logs the member
    /// in; <c>GET /Users/{user}/Items/{item}</c> is the per-user document these
    /// assertions are actually about. The first two are answered with a canned
    /// success so a failure points at the thing under test.</para>
    /// </summary>
    private static bool IsLogin(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath.Contains("AuthenticateByName");

    /// <summary>The provider-id search, which is <c>/Items</c> exactly — the per-user
    /// document lives under <c>/Users/{id}/Items/{id}</c>.</summary>
    private static bool IsProviderSearch(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath == "/Items";

    private const string SearchHit =
        "{\"Items\":[{\"Id\":\"item-1\",\"ProviderIds\":{\"Tmdb\":\"550\"}}]}";

    /// <summary>Answers the search and the login, then the item document.</summary>
    private static Func<HttpRequestMessage, int, HttpResponseMessage> LoginThen(string itemJson) =>
        (req, _) => IsLogin(req) ? Json(GoodLogin)
                  : IsProviderSearch(req) ? Json(SearchHit)
                  : Json(itemJson);

    // ---------------------------------------------------------------- codecs

    /// <summary>
    /// The decision that costs real CPU. "direct" hands the browser the file;
    /// "transcode" starts an ffmpeg job per viewer. Getting it wrong in one
    /// direction burns the server, in the other it plays nothing at all.
    /// </summary>
    [Theory]
    [InlineData("mp4", "h264", "aac", "direct")]
    [InlineData("m4v", "h264", "mp3", "direct")]
    [InlineData("mov", "h264", "aac", "direct")]
    [InlineData("mkv", "h264", "aac", "transcode")]
    [InlineData("avi", "h264", "aac", "transcode")]
    [InlineData("mp4", "hevc", "aac", "transcode")]
    [InlineData("mp4", "h264", "ac3", "transcode")]
    [InlineData("mp4", "h264", "dts", "transcode")]
    public async Task ThePlaybackModeFollowsWhatABrowserCanActuallyDecode(
        string container, string video, string audio, string expected)
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson(container, video, audio)), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.PlaybackMode.Should().Be(expected);
    }

    /// <summary>
    /// Jellyfin reports containers and codecs in whatever case the muxer wrote
    /// them. An allowlist compared case-sensitively would send every uppercase
    /// MP4 to the transcoder.
    /// </summary>
    [Fact]
    public async Task TheCodecAllowlistIgnoresCase()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson("MP4", "H264", "AAC")), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.PlaybackMode.Should().Be("direct");
    }

    /// <summary>
    /// The track the viewer will actually hear decides, not whichever audio
    /// stream ffprobe happened to list first. A file whose first track is AC3
    /// commentary and whose default is AAC plays natively; judging it by the
    /// first track would transcode every such file for nothing.
    /// </summary>
    [Fact]
    public async Task TheDefaultAudioTrackDecidesNotTheFirstOne()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(
            LoginThen(ItemJson("mp4", "h264", defaultAudioCodec: "aac", secondAudioCodec: "ac3")), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.PlaybackMode.Should().Be("direct",
            "the default track is AAC; the AC3 track is an alternate nobody selected");
    }

    /// <summary>
    /// A file with no audio stream at all, or metadata Jellyfin never filled in.
    /// The allowlist is deliberately conservative, so an unknown must land on
    /// "transcode" rather than being waved through as playable.
    /// </summary>
    [Fact]
    public async Task MissingCodecMetadataTranscodesRatherThanGuessing()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson("mp4", "h264", defaultAudioCodec: null)), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.PlaybackMode.Should().Be("transcode");
    }

    // ----------------------------------------------------------- resume time

    /// <summary>
    /// Jellyfin speaks 100-nanosecond ticks; everything above this service speaks
    /// seconds. The conversion is the whole reason resume works, and a factor-of-
    /// ten slip here would put a viewer 20 minutes from where they stopped.
    /// </summary>
    [Fact]
    public async Task TheResumePositionIsConvertedFromTicksToSeconds()
    {
        var owner = UniqueOwner();
        // 90 seconds and a 2-hour runtime, both in ticks.
        var (svc, _) = Build(LoginThen(ItemJson(positionTicks: 900_000_000, runTimeTicks: 72_000_000_000)), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.ResumePositionSeconds.Should().Be(90);
        state.DurationSeconds.Should().Be(7200);
    }

    /// <summary>
    /// A film nobody has started has no <c>UserData</c> at all. That is the
    /// common case, not an error, and it must read as "start from the beginning".
    /// </summary>
    [Fact]
    public async Task AnUnwatchedItemResumesFromZeroRatherThanFailing()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson(positionTicks: null)), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.ResumePositionSeconds.Should().Be(0);
    }

    /// <summary>An unknown runtime is null, not zero — a zero-length film would break a scrubber.</summary>
    [Fact]
    public async Task AnUnknownRuntimeIsNullRatherThanZero()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson(runTimeTicks: null)), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.DurationSeconds.Should().BeNull();
    }

    // --------------------------------------------------------------- tracks

    /// <summary>
    /// The track lists are what the frontend builds its audio and subtitle
    /// pickers from, and <c>Index</c> is what the subtitle proxy needs to fetch
    /// the right one back out. Audio and subtitles must not blend together.
    /// </summary>
    [Fact]
    public async Task AudioAndSubtitleTracksAreReportedSeparatelyWithTheirIndexes()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson()), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state!.AudioTracks.Should().ContainSingle();
        state.AudioTracks[0].Index.Should().Be(2);
        state.AudioTracks[0].IsDefault.Should().BeTrue();
        state.AudioTracks[0].Language.Should().Be("eng");

        state.SubtitleTracks.Should().ContainSingle();
        state.SubtitleTracks[0].Index.Should().Be(3);
        state.SubtitleTracks[0].Title.Should().Be("Spanish");

        state.MediaSourceId.Should().Be("src-9", "the subtitle route needs it alongside the index");
    }

    // ------------------------------------------------------ login and retry

    /// <summary>
    /// A cached token that Jellyfin has since expired. The retry is what stops a
    /// household member being silently signed out mid-film — but only if it
    /// re-authenticates rather than just resending the same dead token.
    /// </summary>
    [Fact]
    public async Task AStaleTokenIsRefreshedAndTheRequestRetriedOnce()
    {
        var owner = UniqueOwner();
        var logins = 0;
        var itemRequests = 0;

        var (svc, _) = Build((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("AuthenticateByName"))
            {
                logins++;
                return Json("{\"AccessToken\":\"tok-" + logins + "\",\"User\":{\"Id\":\"user-1\"}}");
            }
            if (IsProviderSearch(req)) return Json(SearchHit);

            // First per-user item request rejects the cached token; the retry succeeds.
            itemRequests++;
            return itemRequests == 1
                ? Json("{}", HttpStatusCode.Unauthorized)
                : Json(ItemJson());
        }, owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state.Should().NotBeNull("the retry should have produced a usable answer");
        logins.Should().Be(2, "the point of the retry is a fresh login, not a resend");
    }

    /// <summary>
    /// When the credentials themselves have gone bad, the retry cannot help. The
    /// caller must still get the upstream's answer rather than an exception or a
    /// silent null that hides why nothing plays.
    /// </summary>
    [Fact]
    public async Task AnUnauthorizedThatSurvivesTheRefreshIsReportedNotThrown()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build((req, _) => IsLogin(req) ? Json(GoodLogin)
                                       : IsProviderSearch(req) ? Json(SearchHit)
                                       : Json("{}", HttpStatusCode.Unauthorized), owner);

        var act = async () => await svc.GetMoviePlaybackStateAsync(owner, 550);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    /// <summary>
    /// The retry is once, not a loop. An unauthenticated server that answers 401
    /// to the login as well must not send this into a re-authentication cycle.
    /// </summary>
    [Fact]
    public async Task ARejectedLoginDoesNotRetryForever()
    {
        var owner = UniqueOwner();
        var (svc, rec) = Build((req, _) => IsProviderSearch(req)
            ? Json(SearchHit)
            : Json("{}", HttpStatusCode.Unauthorized), owner);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state.Should().BeNull();
        rec.Requests.Should().HaveCountLessThan(5,
            "a login that fails must end the attempt, not restart it");
    }

    /// <summary>
    /// A 200 whose body is missing <c>AccessToken</c> — Jellyfin does this behind
    /// some reverse proxies. Caching a half-built session would poison every later
    /// request for that member until the process restarts.
    /// </summary>
    [Fact]
    public async Task AnIncompleteLoginResponseIsNotCachedAsASession()
    {
        var owner = UniqueOwner();
        var logins = 0;

        var (svc, _) = Build((req, _) =>
        {
            if (IsProviderSearch(req)) return Json(SearchHit);
            if (!IsLogin(req)) return Json(ItemJson());

            logins++;
            // First login is missing the token; the second is well-formed.
            return logins == 1 ? Json("""{"User":{"Id":"user-1"}}""") : Json(GoodLogin);
        }, owner);

        (await svc.GetMoviePlaybackStateAsync(owner, 550)).Should().BeNull();

        var second = await svc.GetMoviePlaybackStateAsync(owner, 550);

        second.Should().NotBeNull("nothing bad was cached, so a good login still works");
        logins.Should().Be(2);
    }

    /// <summary>
    /// A member with no personal Jellyfin login gets the shared-embed path
    /// instead. Asking for their playback state must not turn into a failed login
    /// against Jellyfin on every page load — repeated bad logins are how an
    /// account gets locked out.
    ///
    /// <para>The provider-id lookup still happens: it runs on the server key
    /// before any per-user work, so "no requests at all" would be the wrong
    /// assertion.</para>
    /// </summary>
    [Fact]
    public async Task AMemberWithoutCredentialsIsNeverLoggedIn()
    {
        var owner = UniqueOwner();
        var (svc, rec) = Build(LoginThen(ItemJson()), owner, username: null, password: null);

        var state = await svc.GetMoviePlaybackStateAsync(owner, 550);

        state.Should().BeNull();
        rec.Requests.Where(IsLogin).Should().BeEmpty();
    }

    /// <summary>
    /// The provider-id lookup calls <c>EnsureSuccessStatusCode</c>, so a Jellyfin
    /// that is down throws out of the service rather than returning null. That is
    /// the contract the endpoints are written against — they catch and answer 503
    /// — and it is worth pinning because "returns null" and "throws" are handled
    /// in completely different places.
    /// </summary>
    [Fact]
    public async Task AnUnreachableJellyfinThrowsRatherThanLookingLikeNoSuchMovie()
    {
        var owner = UniqueOwner();
        var (svc, _) = Build((_, _) => Json("{}", HttpStatusCode.ServiceUnavailable), owner);

        var act = async () => await svc.GetMoviePlaybackStateAsync(owner, 550);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// A movie Jellyfin has not scanned yet. No item id means no playback state,
    /// and no login attempt either — there is nothing to ask about.
    /// </summary>
    [Fact]
    public async Task AMovieJellyfinHasNotIndexedYieldsNoStateAndNoLogin()
    {
        var owner = UniqueOwner();
        var (svc, rec) = Build((req, _) => IsProviderSearch(req)
            ? Json("{\"Items\":[]}")
            : Json(GoodLogin), owner);

        (await svc.GetMoviePlaybackStateAsync(owner, 550)).Should().BeNull();
        rec.Requests.Where(IsLogin).Should().BeEmpty();
    }

    /// <summary>
    /// Configuration that names a member but leaves the password blank is the
    /// half-configured case, and it must read as "no credentials" rather than as
    /// a login attempt with an empty password.
    /// </summary>
    [Theory]
    [InlineData("dad", "", false)]
    [InlineData("dad", "   ", false)]
    [InlineData("", "pw", false)]
    [InlineData("dad", "pw", true)]
    public void BlankCredentialsCountAsNoCredentials(string user, string pass, bool expected)
    {
        var owner = UniqueOwner();
        var (svc, _) = Build(LoginThen(ItemJson()), owner, username: user, password: pass);

        svc.HasPersonalCredentials(owner).Should().Be(expected);
    }

    /// <summary>An owner nobody configured is not a household member with a login.</summary>
    [Fact]
    public void AnUnknownOwnerHasNoPersonalCredentials()
    {
        var (svc, _) = Build(LoginThen(ItemJson()));

        svc.HasPersonalCredentials("nobody").Should().BeFalse();
    }

    // ------------------------------------------------------------ HLS proxy

    /// <summary>
    /// Seeking ahead of the transcoder makes Jellyfin kill and restart its ffmpeg
    /// job, and a segment requested in that window comes back non-success even
    /// though nothing is wrong. The single retry is what turns that race into a
    /// brief stall instead of a player error.
    /// </summary>
    [Fact]
    public async Task ASegmentThatLosesTheTranscodeRaceIsRetriedOnce()
    {
        var (svc, rec) = Build((_, n) => n == 0
            ? Json("{}", HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        var result = await svc.ProxyHlsResourceAsync("item-1", "hls1/main/0.ts", null, null);

        result!.StatusCode.Should().Be(200);
        rec.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// The same race can surface as a dropped connection rather than a status.
    /// Both paths must retry, or seeking works only sometimes.
    /// </summary>
    [Fact]
    public async Task ASegmentRequestThatThrowsIsAlsoRetried()
    {
        var (svc, rec) = Build((_, n) => n == 0
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        var result = await svc.ProxyHlsResourceAsync("item-1", "hls1/main/0.ts", null, null);

        result!.StatusCode.Should().Be(200);
        rec.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// A 206 is the normal answer to a range request and must survive the proxy
    /// verbatim — relaying it as 200 breaks seeking in every browser.
    /// </summary>
    [Fact]
    public async Task APartialContentResponseKeepsItsStatusAndRange()
    {
        var (svc, _) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2])
            };
            resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 1, 100);
            return resp;
        });

        var result = await svc.ProxyHlsResourceAsync("item-1", "hls1/main/0.ts", null, "bytes=0-1");

        result!.StatusCode.Should().Be(206);
        result.ContentRange.Should().Contain("0-1/100");
    }

    /// <summary>
    /// <c>subPath</c> is an unvalidated catch-all route parameter that is
    /// interpolated straight into the upstream URL, so this pins where a traversal
    /// attempt actually lands. <see cref="Uri"/> resolves <c>..</c> segments before
    /// the request goes out, which means the path CAN escape <c>/Videos/{itemId}/</c>.
    ///
    /// <para>The reach is bounded by what the request carries: this path sends only
    /// the client identity header, no token of its own, and any <c>api_key</c> in
    /// the query is the caller's own. So it is not a privilege escalation — but it
    /// is not a boundary either, and the difference matters if this route is ever
    /// made anonymous or given a server token. Recorded rather than asserted as
    /// desirable.</para>
    /// </summary>
    [Fact]
    public async Task ASubPathWithTraversalEscapesTheItemPrefix()
    {
        var (svc, rec) = Build((_, _) => Json("{}"));

        await svc.ProxyHlsResourceAsync("item-1", "../../System/Info", null, null);

        rec.Urls.Should().ContainSingle().Which.Should().Be("http://jellyfin.test/System/Info",
            "the item prefix does not survive `..`; if that ever needs to be a boundary, "
            + "reject `..` in subPath at the endpoint");
    }
}
