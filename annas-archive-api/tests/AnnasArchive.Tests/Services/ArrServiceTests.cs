using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Moq.Protected;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// What this app actually asks Sonarr and Radarr to do.
///
/// <para>559 lines across the two with no test naming either, and every method
/// here has a side effect on someone else's disk: adding a movie can start a
/// download, deleting one can leave a torrent seeding forever, and picking the
/// wrong quality profile quietly undoes a library migration one item at a time —
/// a failure this code carries a comment about because it already happened.</para>
///
/// <para>Driven over a stubbed handler that records the whole conversation, so
/// the assertions are about the requests sent rather than a fake's return value.
/// The shared <c>ArrServiceBase</c> rules are exercised through Radarr, since
/// both subclasses inherit the same implementation.</para>
/// </summary>
public class ArrServiceTests
{
    private sealed class Conversation
    {
        public List<(HttpMethod Method, string Url, string? Body)> Sent { get; } = [];

        public IEnumerable<(HttpMethod Method, string Url, string? Body)> To(string fragment) =>
            Sent.Where(r => r.Url.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        public HttpMessageHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                {
                    var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    Sent.Add((req.Method, req.RequestUri!.ToString(), body));
                    return reply(req);
                });
            return mock.Object;
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string RootFolders = """[{"path":"/data/Movies"}]""";

    private const string Profiles = """
        [{"id":1,"name":"Any"},{"id":6,"name":"HD Bluray + WEB"},{"id":9,"name":"WEB-1080p"}]
        """;

    /// <summary>Answers the two defaults lookups, then whatever the caller supplies.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Defaults(
        Func<HttpRequestMessage, HttpResponseMessage>? then = null) =>
        req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("rootfolder")) return Json(RootFolders);
            if (path.Contains("qualityprofile")) return Json(Profiles);
            return then?.Invoke(req) ?? Json("{}");
        };

    private static (RadarrService Svc, Conversation Rec) Radarr(
        Func<HttpRequestMessage, HttpResponseMessage>? reply = null,
        string? qualityProfile = null)
    {
        var rec = new Conversation();
        var http = new HttpClient(rec.Handler(reply ?? Defaults()))
        {
            BaseAddress = new Uri("http://radarr.test")
        };

        var settings = new Dictionary<string, string?> { ["Radarr:ApiKey"] = "k" };
        if (qualityProfile is not null) settings["Radarr:QualityProfile"] = qualityProfile;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (new RadarrService(http, config), rec);
    }

    private static (SonarrService Svc, Conversation Rec) Sonarr(
        Func<HttpRequestMessage, HttpResponseMessage>? reply = null)
    {
        var rec = new Conversation();
        var http = new HttpClient(rec.Handler(reply ?? Defaults()))
        {
            BaseAddress = new Uri("http://sonarr.test")
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sonarr:ApiKey"] = "k" })
            .Build();
        return (new SonarrService(http, config), rec);
    }

    private static JsonObject SentTo(Conversation rec, string fragment, HttpMethod method) =>
        JsonNode.Parse(rec.Sent.Last(r => r.Method == method && r.Url.Contains(fragment)).Body!)!.AsObject();

    // ------------------------------------------------- adding without grabbing

    /// <summary>
    /// The catalog-only add. Cataloguing a film someone might want later must not
    /// start downloading it — otherwise browsing the catalogue fills the disk.
    /// </summary>
    [Fact]
    public async Task ACatalogOnlyAddIsNeitherMonitoredNorSearched()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")));

        await svc.AddMovieAsync(new JsonObject { ["title"] = "Some Film" }, MovieAddMode.CatalogOnly);

        var sent = SentTo(rec, "/api/v3/movie", HttpMethod.Post);
        sent["monitored"]!.GetValue<bool>().Should().BeFalse();
        sent["addOptions"]!["searchForMovie"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>The other half: an explicit request really does start the download.</summary>
    [Fact]
    public async Task AMonitorAndSearchAddDoesStartLookingForIt()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")));

        await svc.AddMovieAsync(new JsonObject { ["title"] = "Some Film" }, MovieAddMode.MonitorAndSearch);

        var sent = SentTo(rec, "/api/v3/movie", HttpMethod.Post);
        sent["monitored"]!.GetValue<bool>().Should().BeTrue();
        sent["addOptions"]!["searchForMovie"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TagsAreAttachedAtAddTimeWhenGiven()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")));

        await svc.AddMovieAsync(new JsonObject { ["title"] = "X" }, MovieAddMode.CatalogOnly, tagIds: [3, 7]);

        SentTo(rec, "/api/v3/movie", HttpMethod.Post)["tags"]!.AsArray()
            .Select(t => t!.GetValue<int>()).Should().BeEquivalentTo([3, 7]);
    }

    // ------------------------------------------------------ quality profile

    /// <summary>
    /// The profile is matched <b>by name</b>, not by taking the first one.
    ///
    /// <para>Sonarr and Radarr return profiles in id order, so <c>profiles[0]</c> is
    /// whichever is oldest — on a stock install that is "Any", which accepts 4K and
    /// other oversized releases. Everything added through this app was landing on
    /// that profile regardless of the 1080p profiles the library had been migrated
    /// onto, so new adds quietly undid the migration one film at a time.</para>
    /// </summary>
    [Fact]
    public async Task TheQualityProfileIsChosenByNameNotByBeingFirstInTheList()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")));

        await svc.AddMovieAsync(new JsonObject { ["title"] = "X" }, MovieAddMode.CatalogOnly);

        SentTo(rec, "/api/v3/movie", HttpMethod.Post)["qualityProfileId"]!.GetValue<int>()
            .Should().Be(6, "'HD Bluray + WEB' is the configured default, not 'Any' at id 1");
    }

    [Fact]
    public async Task AConfiguredProfileNameOverridesTheDefault()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")), qualityProfile: "WEB-1080p");

        await svc.AddMovieAsync(new JsonObject { ["title"] = "X" }, MovieAddMode.CatalogOnly);

        SentTo(rec, "/api/v3/movie", HttpMethod.Post)["qualityProfileId"]!.GetValue<int>().Should().Be(9);
    }

    /// <summary>
    /// A wrong profile still beats being unable to add anything, so an unresolvable
    /// name falls back rather than failing.
    /// </summary>
    [Fact]
    public async Task AnUnknownProfileNameFallsBackRatherThanRefusingToAdd()
    {
        var (svc, rec) = Radarr(Defaults(_ => Json("""{"id":1}""")), qualityProfile: "No Such Profile");

        await svc.AddMovieAsync(new JsonObject { ["title"] = "X" }, MovieAddMode.CatalogOnly);

        SentTo(rec, "/api/v3/movie", HttpMethod.Post)["qualityProfileId"]!.GetValue<int>().Should().Be(1);
    }

    /// <summary>
    /// A misconfigured Radarr is named as such rather than producing a confusing
    /// failure deeper in — the message has to tell someone what to go and fix.
    /// </summary>
    [Fact]
    public async Task AnUnconfiguredRootFolderSaysSoInsteadOfFailingObscurely()
    {
        var (svc, _) = Radarr(req => req.RequestUri!.AbsolutePath.Contains("rootfolder")
            ? Json("[]")
            : Json(Profiles));

        var act = async () => await svc.AddMovieAsync(new JsonObject(), MovieAddMode.CatalogOnly);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*root folder*");
    }

    // ------------------------------------------------------------- tags

    /// <summary>
    /// Radarr lowercases tag labels on create, so a case-sensitive match would
    /// never find the tag it just made and would create a new one on every single
    /// call.
    /// </summary>
    [Fact]
    public async Task AnExistingTagIsFoundRegardlessOfTheCaseRadarrStoredItIn()
    {
        var (svc, rec) = Radarr(req => req.RequestUri!.AbsolutePath.Contains("/tag")
            ? Json("""[{"id":4,"label":"date-night-pool"}]""")
            : Json("{}"));

        var id = await svc.EnsureTagAsync("Date-Night-Pool");

        id.Should().Be(4);
        rec.Sent.Where(r => r.Method == HttpMethod.Post).Should().BeEmpty(
            "the tag already existed, so creating another would leave two");
    }

    [Fact]
    public async Task AMissingTagIsCreatedOnce()
    {
        var (svc, rec) = Radarr(req => req.Method == HttpMethod.Post
            ? Json("""{"id":11,"label":"date-night-pool"}""")
            : Json("[]"));

        var id = await svc.EnsureTagAsync("date-night-pool");

        id.Should().Be(11);
        rec.To("/api/v3/tag").Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    // --------------------------------------------------------- bulk edits

    /// <summary>
    /// Radarr's editor endpoint treats the tag modes as mutually exclusive per
    /// request, so adding and removing tags cannot ride in one call.
    /// </summary>
    [Fact]
    public async Task AddingAndRemovingTagsAreSeparateRequests()
    {
        var (svc, rec) = Radarr();

        await svc.EditMoviesAsync([1, 2], addTagIds: [5], removeTagIds: [6]);

        var edits = rec.To("/api/v3/movie/editor").ToList();
        edits.Should().HaveCount(2);
        edits.Select(e => JsonNode.Parse(e.Body!)!["applyTags"]!.ToString())
            .Should().BeEquivalentTo("add", "remove");
    }

    [Fact]
    public async Task ChangingOnlyMonitoringSendsOnlyThatOneRequest()
    {
        var (svc, rec) = Radarr();

        await svc.EditMoviesAsync([1], monitored: true);

        var edits = rec.To("/api/v3/movie/editor").ToList();
        edits.Should().ContainSingle();
        JsonNode.Parse(edits[0].Body!)!["monitored"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>An empty selection must not send a request that means "all of them".</summary>
    [Fact]
    public async Task EditingNoMoviesSendsNothingAtAll()
    {
        var (svc, rec) = Radarr();

        await svc.EditMoviesAsync([], monitored: true, addTagIds: [1]);

        rec.Sent.Should().BeEmpty();
    }

    // ------------------------------------------------------------ deletion

    /// <summary>
    /// Deleting the record alone leaves anything already grabbed running in the
    /// download client, orphaned, forever. The queue is cleared first — and the
    /// order matters, because doing it afterwards would race the deletion.
    /// </summary>
    [Fact]
    public async Task DeletingAMovieClearsItsDownloadQueueFirst()
    {
        var (svc, rec) = Radarr(req => req.RequestUri!.AbsolutePath.Contains("queue")
            ? Json("""{"records":[]}""")
            : Json("{}"));

        await svc.DeleteMovieAsync(42);

        var queueIndex = rec.Sent.FindIndex(r => r.Url.Contains("queue"));
        var deleteIndex = rec.Sent.FindIndex(r => r.Method == HttpMethod.Delete && r.Url.Contains("/movie/42"));

        queueIndex.Should().BeGreaterThanOrEqualTo(0, "the queue must be consulted");
        deleteIndex.Should().BeGreaterThan(queueIndex, "clearing after deleting would race it");
    }

    /// <summary>The files go too — leaving them would fill the disk with orphans.</summary>
    [Fact]
    public async Task DeletingAMovieAlsoDeletesItsFiles()
    {
        var (svc, rec) = Radarr(req => req.RequestUri!.AbsolutePath.Contains("queue")
            ? Json("""{"records":[]}""")
            : Json("{}"));

        await svc.DeleteMovieAsync(42);

        rec.Sent.Should().Contain(r => r.Method == HttpMethod.Delete && r.Url.Contains("deleteFiles=true"));
    }

    /// <summary>
    /// A movie that is already gone is not an error — the caller wanted it absent
    /// and it is absent.
    /// </summary>
    [Fact]
    public async Task AskingForAMovieRadarrDoesNotHaveReturnsNothingRatherThanThrowing()
    {
        var (svc, _) = Radarr(_ => Json("{}", HttpStatusCode.NotFound));

        (await svc.GetMovieAsync(999)).Should().BeNull();
    }

    // ------------------------------------------------------------- Sonarr

    /// <summary>
    /// Picking no seasons means "all of them" — the caller did not express a
    /// preference, so Sonarr's own defaults stand untouched.
    /// </summary>
    [Fact]
    public async Task AddingASeriesWithoutPickingSeasonsLeavesEverySeasonAsSonarrSentIt()
    {
        var (svc, rec) = Sonarr(Defaults(_ => Json("""{"id":1}""")));

        var series = new JsonObject
        {
            ["title"] = "A Show",
            ["seasons"] = new JsonArray(
                new JsonObject { ["seasonNumber"] = 1, ["monitored"] = true },
                new JsonObject { ["seasonNumber"] = 2, ["monitored"] = false })
        };

        await svc.AddSeriesAsync(series);

        var seasons = SentTo(rec, "/api/v3/series", HttpMethod.Post)["seasons"]!.AsArray();
        seasons[0]!["monitored"]!.GetValue<bool>().Should().BeTrue();
        seasons[1]!["monitored"]!.GetValue<bool>().Should().BeFalse("untouched, not forced on");
    }

    /// <summary>Picking a subset monitors exactly that subset.</summary>
    [Fact]
    public async Task AddingASeriesWithAChosenSubsetMonitorsOnlyThose()
    {
        var (svc, rec) = Sonarr(Defaults(_ => Json("""{"id":1}""")));

        var series = new JsonObject
        {
            ["title"] = "A Show",
            ["seasons"] = new JsonArray(
                new JsonObject { ["seasonNumber"] = 1, ["monitored"] = true },
                new JsonObject { ["seasonNumber"] = 2, ["monitored"] = true },
                new JsonObject { ["seasonNumber"] = 3, ["monitored"] = true })
        };

        await svc.AddSeriesAsync(series, monitoredSeasons: [2]);

        var seasons = SentTo(rec, "/api/v3/series", HttpMethod.Post)["seasons"]!.AsArray();
        seasons[0]!["monitored"]!.GetValue<bool>().Should().BeFalse();
        seasons[1]!["monitored"]!.GetValue<bool>().Should().BeTrue();
        seasons[2]!["monitored"]!.GetValue<bool>().Should().BeFalse();
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> SeriesWith(string seasonsJson) =>
        req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/series/")
            ? Json($$"""{"id":5,"title":"A Show","seasons":{{seasonsJson}}}""")
            : Json("""{"id":5}""");

    /// <summary>
    /// Updating seasons is <b>additive</b>: it never un-monitors something. One
    /// person adding season 3 must not silently drop the season 1 someone else is
    /// already waiting on.
    /// </summary>
    [Fact]
    public async Task UpdatingSeasonsNeverUnMonitorsOneThatWasAlreadyOn()
    {
        var (svc, rec) = Sonarr(SeriesWith(
            """[{"seasonNumber":1,"monitored":true},{"seasonNumber":3,"monitored":false}]"""));

        await svc.UpdateSeriesSeasonsAsync(5, [3]);

        var seasons = SentTo(rec, "/api/v3/series/5", HttpMethod.Put)["seasons"]!.AsArray();
        seasons[0]!["monitored"]!.GetValue<bool>().Should().BeTrue("season 1 was already monitored");
        seasons[1]!["monitored"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>
    /// Adding a series triggers a search through <c>addOptions</c>; updating an
    /// existing one does not. Without an explicit command per newly-monitored
    /// season they would sit monitored but never actually get grabbed.
    /// </summary>
    [Fact]
    public async Task ANewlyMonitoredSeasonGetsItsOwnSearchCommand()
    {
        var (svc, rec) = Sonarr(SeriesWith(
            """[{"seasonNumber":1,"monitored":true},{"seasonNumber":3,"monitored":false}]"""));

        await svc.UpdateSeriesSeasonsAsync(5, [3]);

        var commands = rec.To("/api/v3/command").ToList();
        commands.Should().ContainSingle("only season 3 is newly monitored");

        var command = JsonNode.Parse(commands[0].Body!)!;
        command["name"]!.ToString().Should().Be("SeasonSearch");
        command["seasonNumber"]!.GetValue<int>().Should().Be(3);
    }

    /// <summary>
    /// A season already monitored gets no second search — re-requesting it would
    /// hammer the indexers for something already in hand.
    /// </summary>
    [Fact]
    public async Task AlreadyMonitoredSeasonsAreNotSearchedAgain()
    {
        var (svc, rec) = Sonarr(SeriesWith("""[{"seasonNumber":1,"monitored":true}]"""));

        await svc.UpdateSeriesSeasonsAsync(5, [1]);

        rec.To("/api/v3/command").Should().BeEmpty();
    }

    // ------------------------------------------------- deleting a series or season

    /// <summary>
    /// A series delete has to clear the download queue first. Deleting the record
    /// alone leaves whatever was already grabbed running in qBittorrent/SABnzbd,
    /// orphaned and seeding forever with nothing left in Sonarr that refers to it.
    /// The order matters: clearing afterwards would need the series to look up.
    /// </summary>
    [Fact]
    public async Task DeletingASeriesClearsItsDownloadQueueFirst()
    {
        var (svc, rec) = Sonarr(Defaults(req =>
            req.RequestUri!.AbsolutePath.Contains("/queue")
                ? Json("""{"records":[{"id":77,"seriesId":5}]}""")
                : Json("{}")));

        await svc.DeleteSeriesAsync(5);

        var queueRemoval = rec.Sent.FindIndex(
            r => r.Method == HttpMethod.Delete && r.Url.Contains("/queue/77"));
        var seriesDelete = rec.Sent.FindIndex(
            r => r.Method == HttpMethod.Delete && r.Url.Contains("/series/5"));

        queueRemoval.Should().BeGreaterThanOrEqualTo(0, "the queued grab must be cancelled");
        seriesDelete.Should().BeGreaterThan(queueRemoval,
            "the queue is cleared before the series that identifies it is gone");
    }

    /// <summary>Deleting a series is meant to reclaim the disk, so the files go too —
    /// without this the record vanishes and the episodes stay, invisible to Sonarr
    /// and to the library page that lists what is downloaded.</summary>
    [Fact]
    public async Task DeletingASeriesAlsoDeletesItsFiles()
    {
        var (svc, rec) = Sonarr();

        await svc.DeleteSeriesAsync(5);

        rec.Sent.Should().Contain(r =>
            r.Method == HttpMethod.Delete && r.Url.Contains("deleteFiles=true"));
    }

    /// <summary>A Sonarr that refuses the delete must not be reported as success —
    /// the caller turns this into a 502, and swallowing it would tell someone their
    /// series was destroyed while it is still there.</summary>
    [Fact]
    public async Task ASeriesDeleteSonarrRefusesIsRaisedRatherThanSwallowed()
    {
        var (svc, _) = Sonarr(Defaults(req =>
            req.Method == HttpMethod.Delete
                ? Json("""{"message":"nope"}""", HttpStatusCode.InternalServerError)
                : Json("{}")));

        var delete = async () => await svc.DeleteSeriesAsync(5);

        await delete.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>Episode files across three seasons, so a season delete has something
    /// to pick wrongly from.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> SeriesWithEpisodeFiles() =>
        Defaults(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/episodefile"))
                return Json("""
                    [{"id":11,"seasonNumber":1},
                     {"id":21,"seasonNumber":2},
                     {"id":22,"seasonNumber":2},
                     {"id":31,"seasonNumber":3}]
                    """);
            if (req.RequestUri.AbsolutePath.Contains("/series/"))
                return Json("""
                    {"id":5,"seasons":[
                        {"seasonNumber":1,"monitored":true},
                        {"seasonNumber":2,"monitored":true},
                        {"seasonNumber":3,"monitored":true}]}
                    """);
            return Json("{}");
        });

    /// <summary>
    /// <b>The season filter is the entire safety mechanism.</b> Sonarr has no
    /// delete-a-season endpoint, so this deletes episode files one at a time from a
    /// list covering the whole series. If the filter went, deleting season 2 would
    /// take every episode of every season with it — and the caller would see the same
    /// 204 either way.
    /// </summary>
    [Fact]
    public async Task DeletingASeasonDeletesOnlyThatSeasonsEpisodeFiles()
    {
        var (svc, rec) = Sonarr(SeriesWithEpisodeFiles());

        await svc.DeleteSeasonAsync(5, 2);

        var deleted = rec.Sent
            .Where(r => r.Method == HttpMethod.Delete && r.Url.Contains("/episodefile/"))
            .Select(r => r.Url.Split("/episodefile/")[1])
            .ToList();

        deleted.Should().BeEquivalentTo(["21", "22"],
            "only season 2's files, and both of them");
    }

    /// <summary>
    /// The season is un-monitored after its files go, or Sonarr immediately
    /// re-downloads exactly what was just deleted — the delete would appear to do
    /// nothing at all, some minutes later.
    /// </summary>
    [Fact]
    public async Task DeletingASeasonUnMonitorsItSoSonarrDoesNotFetchItStraightBack()
    {
        var (svc, rec) = Sonarr(SeriesWithEpisodeFiles());

        await svc.DeleteSeasonAsync(5, 2);

        var seasons = SentTo(rec, "/series/5", HttpMethod.Put)["seasons"]!.AsArray();
        seasons.Single(s => (int)s!["seasonNumber"]! == 2)!["monitored"]!
            .GetValue<bool>().Should().BeFalse();
    }

    /// <summary>
    /// And only that season. The whole series object is sent back on the PUT, so a
    /// careless loop would un-monitor everything — quietly stopping the show from
    /// ever updating again, which nobody notices until a season is missing.
    /// </summary>
    [Fact]
    public async Task UnMonitoring_a_season_leaves_the_other_seasons_monitored()
    {
        var (svc, rec) = Sonarr(SeriesWithEpisodeFiles());

        await svc.DeleteSeasonAsync(5, 2);

        var seasons = SentTo(rec, "/series/5", HttpMethod.Put)["seasons"]!.AsArray();
        seasons.Where(s => (int)s!["seasonNumber"]! != 2)
            .Should().OnlyContain(s => s!["monitored"]!.GetValue<bool>());
    }

    /// <summary>
    /// One file Sonarr will not delete must not abort the rest. Throwing here would
    /// leave the season half-deleted <i>and still monitored</i>, so Sonarr would
    /// re-fetch the episodes that did go — the worst of both outcomes.
    /// </summary>
    [Fact]
    public async Task AnEpisodeFileThatWillNotDeleteDoesNotStrandTheSeasonMonitored()
    {
        var (svc, rec) = Sonarr(Defaults(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/episodefile/21") && req.Method == HttpMethod.Delete)
                return Json("""{"message":"locked"}""", HttpStatusCode.Conflict);
            if (req.RequestUri.AbsolutePath.Contains("/episodefile"))
                return Json("""[{"id":21,"seasonNumber":2},{"id":22,"seasonNumber":2}]""");
            if (req.RequestUri.AbsolutePath.Contains("/series/"))
                return Json("""{"id":5,"seasons":[{"seasonNumber":2,"monitored":true}]}""");
            return Json("{}");
        }));

        var delete = async () => await svc.DeleteSeasonAsync(5, 2);

        await delete.Should().NotThrowAsync();
        rec.Sent.Should().Contain(r =>
            r.Method == HttpMethod.Delete && r.Url.Contains("/episodefile/22"),
            "the failure of one file must not stop the next");
        SentTo(rec, "/series/5", HttpMethod.Put)["seasons"]!.AsArray()
            .Single()!["monitored"]!.GetValue<bool>().Should().BeFalse(
                "the un-monitor still has to happen, or Sonarr re-fetches what did delete");
    }

    /// <summary>A season with nothing downloaded still gets un-monitored — the point
    /// of the action is "stop bringing me this", not only "remove these files".</summary>
    [Fact]
    public async Task DeletingASeasonWithNoFilesStillUnMonitorsIt()
    {
        var (svc, rec) = Sonarr(Defaults(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/episodefile"))
                return Json("[]");
            if (req.RequestUri.AbsolutePath.Contains("/series/"))
                return Json("""{"id":5,"seasons":[{"seasonNumber":2,"monitored":true}]}""");
            return Json("{}");
        }));

        await svc.DeleteSeasonAsync(5, 2);

        rec.Sent.Should().NotContain(r => r.Url.Contains("/episodefile/"));
        SentTo(rec, "/series/5", HttpMethod.Put)["seasons"]!.AsArray()
            .Single()!["monitored"]!.GetValue<bool>().Should().BeFalse();
    }
}
