using System.Text.Json.Nodes;
using AnnasArchive.API.Endpoints;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// The display decisions behind the Date Night screens. These were private statics in
/// an 822-line endpoint file, so the only way to reach them was an HTTP request with a
/// Radarr instance behind it.
///
/// Most of what is pinned here is behaviour under *missing* data — a movie deleted from
/// Radarr mid-week, a poster with no remote URL, a movie whose title only the untagged
/// list still knows. Those are the cases that produce a wrong screen rather than an
/// error, so nothing else catches them.
/// </summary>
public class DateNightViewsTests
{
    private static JsonObject Movie(
        int id, string? title = "A Movie", string[]? genres = null,
        string? remoteUrl = null, string? localUrl = null,
        int? year = 2024, int? tmdbId = 99, bool hasFile = false, bool monitored = false,
        string? overview = "An overview")
    {
        var movie = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["year"] = year,
            ["tmdbId"] = tmdbId,
            ["hasFile"] = hasFile,
            ["monitored"] = monitored,
            ["overview"] = overview
        };

        if (genres is not null)
            movie["genres"] = new JsonArray(genres.Select(g => (JsonNode?)g).ToArray());

        if (remoteUrl is not null || localUrl is not null)
        {
            var poster = new JsonObject { ["coverType"] = "poster" };
            if (remoteUrl is not null) poster["remoteUrl"] = remoteUrl;
            if (localUrl is not null) poster["url"] = localUrl;
            movie["images"] = new JsonArray(poster);
        }

        return movie;
    }

    // ─── GenreLine ────────────────────────────────────────────────────────

    [Fact]
    public void GenreLine_JoinsGenresWithASeparator()
    {
        DateNightViews.GenreLine(Movie(1, genres: ["Drama", "Thriller"]))
            .Should().Be("Drama · Thriller");
    }

    /// <summary>The card has one line for this; six genres would push the layout apart.</summary>
    [Fact]
    public void GenreLine_KeepsOnlyTheFirstTwo()
    {
        DateNightViews.GenreLine(Movie(1, genres: ["Drama", "Thriller", "Crime", "Mystery"]))
            .Should().Be("Drama · Thriller");
    }

    [Fact]
    public void GenreLine_SkipsBlankGenreEntries()
    {
        DateNightViews.GenreLine(Movie(1, genres: ["", "  ", "Drama"])).Should().Be("Drama");
    }

    [Fact]
    public void GenreLine_IsNullWhenThereAreNoGenres()
    {
        DateNightViews.GenreLine(Movie(1, genres: [])).Should().BeNull();
        DateNightViews.GenreLine(Movie(1)).Should().BeNull();
    }

    // ─── PosterUrl ────────────────────────────────────────────────────────

    /// <summary>
    /// Radarr's own <c>url</c> sits behind its API key, so a browser renders it as a
    /// broken image. Preferring the remote (TMDB) one is the whole point.
    /// </summary>
    [Fact]
    public void PosterUrl_PrefersTheRemoteUrlABrowserCanActuallyLoad()
    {
        DateNightViews.PosterUrl(Movie(1, remoteUrl: "https://image.tmdb.org/p.jpg", localUrl: "/MediaCover/1/poster.jpg"))
            .Should().Be("https://image.tmdb.org/p.jpg");
    }

    [Fact]
    public void PosterUrl_FallsBackToTheLocalUrlWhenThereIsNoRemoteOne()
    {
        DateNightViews.PosterUrl(Movie(1, localUrl: "/MediaCover/1/poster.jpg"))
            .Should().Be("/MediaCover/1/poster.jpg");
    }

    [Fact]
    public void PosterUrl_IgnoresImagesThatAreNotPosters()
    {
        var movie = Movie(1);
        movie["images"] = new JsonArray(
            new JsonObject { ["coverType"] = "fanart", ["remoteUrl"] = "https://fanart" },
            new JsonObject { ["coverType"] = "poster", ["remoteUrl"] = "https://poster" });

        DateNightViews.PosterUrl(movie).Should().Be("https://poster");
    }

    [Fact]
    public void PosterUrl_IsNullWhenTheMovieHasNoImages()
    {
        DateNightViews.PosterUrl(Movie(1)).Should().BeNull();
    }

    // ─── ResolveCycleMovies ───────────────────────────────────────────────

    private static WeeklyCycle CycleWith(List<int> movieIds, Dictionary<string, Dictionary<int, string>>? votes = null) =>
        new("2026-07-27", movieIds, DateTime.UtcNow, DateTime.UtcNow.AddDays(6), "Active",
            new Dictionary<string, Dictionary<int, string>>(votes ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            null, null, DateNightPolicy.NewSchedule());

    private static string? NoSummary(int _) => null;

    [Fact]
    public void ResolveCycleMovies_KeepsTheDrawOrderRatherThanThePoolOrder()
    {
        var view = DateNightViews.ResolveCycleMovies(
            CycleWith([3, 1, 2]),
            [Movie(1, "One"), Movie(2, "Two"), Movie(3, "Three")],
            NoSummary);

        view.Select(v => v.Title).Should().Equal("Three", "One", "Two");
    }

    /// <summary>
    /// A movie can be deleted in Radarr, or lose its tag, between the draw and the
    /// render. Dropping the row would silently shorten the ballot, leaving a week that
    /// can never reach "everyone voted" — so it stays, under a placeholder title.
    /// </summary>
    [Fact]
    public void ResolveCycleMovies_StillProducesARowForAMovieThatLeftThePool()
    {
        var view = DateNightViews.ResolveCycleMovies(CycleWith([1, 412]), [Movie(1, "One")], NoSummary);

        view.Should().HaveCount(2);
        view[1].MovieId.Should().Be(412);
        view[1].Title.Should().Be("#412");
        view[1].PosterUrl.Should().BeNull();
        view[1].Year.Should().BeNull();
        view[1].HasFile.Should().BeFalse();
        view[1].Monitored.Should().BeFalse();
    }

    [Fact]
    public void ResolveCycleMovies_CarriesEachPersonsVoteOntoTheRightMovie()
    {
        var view = DateNightViews.ResolveCycleMovies(
            CycleWith([1, 2], new()
            {
                ["Mom"] = new() { [1] = "Up", [2] = "Never" },
                ["Dad"] = new() { [2] = "Down" }
            }),
            [Movie(1), Movie(2)], NoSummary);

        view[0].MomVote.Should().Be("Up");
        view[0].DadVote.Should().BeNull();   // Dad has not voted on this one yet
        view[1].MomVote.Should().Be("Never");
        view[1].DadVote.Should().Be("Down");
    }

    [Fact]
    public void ResolveCycleMovies_AttachesTheCachedSummaryForEachMovie()
    {
        var view = DateNightViews.ResolveCycleMovies(
            CycleWith([7]), [Movie(7)], id => id == 7 ? "A cached summary" : null);

        view[0].Summary.Should().Be("A cached summary");
    }

    [Fact]
    public void ResolveCycleMovies_CopiesTheRadarrFlagsThroughToTheView()
    {
        var view = DateNightViews.ResolveCycleMovies(
            CycleWith([1]),
            [Movie(1, "One", genres: ["Drama"], remoteUrl: "https://p.jpg",
                   year: 1999, tmdbId: 603, hasFile: true, monitored: true)],
            NoSummary);

        view[0].Should().BeEquivalentTo(new
        {
            MovieId = 1, Title = "One", PosterUrl = "https://p.jpg", TmdbId = 603,
            Year = 1999, Genre = "Drama", HasFile = true, Monitored = true
        });
    }

    [Fact]
    public void ResolveCycleMovies_IgnoresPoolEntriesWithNoUsableId()
    {
        var malformed = new JsonObject { ["title"] = "No id here" };

        var act = () => DateNightViews.ResolveCycleMovies(CycleWith([1]), [malformed, Movie(1)], NoSummary);

        act.Should().NotThrow();
        act().Should().ContainSingle().Which.Title.Should().Be("A Movie");
    }

    [Fact]
    public void ResolveCycleMovies_ReturnsNothingForAnEmptyDraw()
    {
        DateNightViews.ResolveCycleMovies(CycleWith([]), [Movie(1)], NoSummary).Should().BeEmpty();
    }

    // ─── Recoverable list ─────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Cutoff => Now - DateNightPolicy.CoolingOff;

    private static MovieListEntry Entry(
        bool never = false, DateTime? neverUtc = null,
        bool watched = false, DateTime? watchedUtc = null,
        DateTime? disagreedUtc = null) =>
        new(never, neverUtc, watched, watchedUtc, disagreedUtc, null);

    [Theory]
    [InlineData(true, false, false, true)]   // never show again
    [InlineData(false, true, false, true)]   // watched
    [InlineData(false, false, true, true)]   // disagreed, still cooling off
    [InlineData(false, false, false, false)] // nothing holds it out
    public void IsHeldOut_CoversEachWayAMovieLeavesTheDraw(
        bool never, bool watched, bool recentlyDisagreed, bool expected)
    {
        var entry = Entry(never: never, watched: watched,
            disagreedUtc: recentlyDisagreed ? Now.AddDays(-1) : null);

        DateNightViews.IsHeldOut(entry, Cutoff).Should().Be(expected);
    }

    /// <summary>
    /// The one that lets a movie return on its own. A disagreement older than the
    /// cooling-off window stops holding it out, unlike never-show and watched.
    /// </summary>
    [Fact]
    public void IsHeldOut_ReleasesADisagreementOnceTheCoolingOffWindowHasPassed()
    {
        var stale = Entry(disagreedUtc: Cutoff.AddDays(-1));

        DateNightViews.IsHeldOut(stale, Cutoff).Should().BeFalse();
    }

    /// <summary>
    /// Titles come from the full Radarr list, not the pool: a watched movie has already
    /// lost its pool tag, so looking there would show "#412" for exactly the entries
    /// someone most wants to recover.
    /// </summary>
    [Fact]
    public void RecoverableMovies_TitlesAWatchedMovieThatIsNoLongerInThePool()
    {
        var recoverable = DateNightViews.RecoverableMovies(
            new Dictionary<int, MovieListEntry> { [412] = Entry(watched: true, watchedUtc: Now) },
            new Dictionary<int, string> { [412] = "Dune" },
            Cutoff);

        recoverable.Should().ContainSingle();
        recoverable[0].Title.Should().Be("Dune");
        recoverable[0].Reason.Should().Be("Watched — retired from pool");
    }

    [Fact]
    public void RecoverableMovies_FallsBackToTheIdWhenNoTitleIsKnownAnywhere()
    {
        var recoverable = DateNightViews.RecoverableMovies(
            new Dictionary<int, MovieListEntry> { [412] = Entry(never: true, neverUtc: Now) },
            new Dictionary<int, string>(),
            Cutoff);

        recoverable[0].Title.Should().Be("#412");
    }

    /// <summary>Never-show wins the label: it is the deliberate choice, and the one a
    /// person is looking for when they come to undo something.</summary>
    [Fact]
    public void RecoverableMovies_ReportsNeverShowAheadOfWatchedWhenBothAreSet()
    {
        var recoverable = DateNightViews.RecoverableMovies(
            new Dictionary<int, MovieListEntry>
            {
                [1] = Entry(never: true, neverUtc: Now, watched: true, watchedUtc: Now)
            },
            new Dictionary<int, string> { [1] = "Both" },
            Cutoff);

        recoverable[0].Reason.Should().Be("Never show again");
    }

    [Fact]
    public void RecoverableMovies_ShowsTheMostRecentlyHeldOutFirst()
    {
        var recoverable = DateNightViews.RecoverableMovies(
            new Dictionary<int, MovieListEntry>
            {
                [1] = Entry(never: true, neverUtc: Now.AddDays(-10)),
                [2] = Entry(never: true, neverUtc: Now.AddDays(-1)),
                [3] = Entry(never: true, neverUtc: Now.AddDays(-5))
            },
            new Dictionary<int, string> { [1] = "Old", [2] = "Newest", [3] = "Middle" },
            Cutoff);

        recoverable.Select(r => r.Title).Should().Equal("Newest", "Middle", "Old");
    }

    [Fact]
    public void RecoverableMovies_OmitsMoviesThatAreStillInTheDraw()
    {
        var recoverable = DateNightViews.RecoverableMovies(
            new Dictionary<int, MovieListEntry> { [1] = Entry(), [2] = Entry(watched: true, watchedUtc: Now) },
            new Dictionary<int, string> { [1] = "Eligible", [2] = "Watched" },
            Cutoff);

        recoverable.Select(r => r.MovieId).Should().Equal(2);
    }

    // ─── NoCycleStatus ────────────────────────────────────────────────────

    [Fact]
    public void NoCycleStatus_ReportsSkippedWhileASkipIsInEffect()
    {
        var skip = new SkipState(Now.AddDays(3), "Paul", Now);

        DateNightViews.NoCycleStatus(skip, isTest: false, Now).Should().Be("Skipped");
    }

    [Fact]
    public void NoCycleStatus_ReportsNoneOnceTheSkipHasExpired()
    {
        var skip = new SkipState(Now.AddDays(-1), "Paul", Now.AddDays(-8));

        DateNightViews.NoCycleStatus(skip, isTest: false, Now).Should().Be("None");
    }

    /// <summary>
    /// The dry run has no calendar week to skip. Showing "Skipped" there would suggest
    /// the test run was blocked when it simply has not been drawn yet.
    /// </summary>
    [Fact]
    public void NoCycleStatus_NeverReportsSkippedForTheDryRun()
    {
        var skip = new SkipState(Now.AddDays(3), "Paul", Now);

        DateNightViews.NoCycleStatus(skip, isTest: true, Now).Should().Be("None");
    }

    [Fact]
    public void NoCycleStatus_ReportsNoneWhenNoSkipWasEverSet()
    {
        DateNightViews.NoCycleStatus(new SkipState(null, null, null), isTest: false, Now)
            .Should().Be("None");
    }
}
