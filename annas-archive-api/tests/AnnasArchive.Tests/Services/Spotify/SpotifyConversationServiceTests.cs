using Moq;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The phase-3 gate: the screenshot questions have to produce answers that are
/// both useful and true. Most of these assert on wording, because wording is the
/// product here — "0 tracks" and "I cannot see inside it" are the same data and
/// very different answers.
/// </summary>
public class SpotifyConversationServiceTests
{
    // ─── "What songs are in Lucy + Laura?" ───────────────────────────────────

    [Fact]
    public async Task ListsTheItemsOfAPlaylistTheUserOwns()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Lucy + Laura"),
            spotify => spotify
                .WithPlaylists(Owned("p1", "Lucy + Laura", 2))
                .WithItems("p1", Page(Track("Mystery Train"), Track("Cross Road Blues"))));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what songs are in Lucy + Laura"));

        response.Message.Should().Contain("Lucy + Laura").And.Contain("2");
        response.Data.Should().BeOfType<SpotifyPlaylistItemsPageDto>()
            .Which.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExplainsTheRestrictionInsteadOfShowingAnEmptyListWhenSpotifyRefuses()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Someone Elses"),
            spotify => spotify
                .WithPlaylists(Followed("p9", "Someone Elses"))
                .WithItems("p9", Forbidden("p9")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what is in Someone Elses"));

        response.Message.Should().Contain("follow").And.Contain("not");
        response.Message.Should().NotContain("empty");
        response.Message.Should().NotContain("0 items");
    }

    [Fact]
    public async Task SaysAnEmptyPlaylistIsEmptyOnlyWhenSpotifyActuallySaysSo()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Nothing Here"),
            spotify => spotify
                .WithPlaylists(Owned("p2", "Nothing Here", 0))
                .WithItems("p2", Page()));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what is in Nothing Here"));

        response.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task DistinguishesUnavailableContentsFromAnEmptyPlaylist()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Mystery"),
            spotify => spotify
                .WithPlaylists(Owned("p3", "Mystery", null))
                .WithItems("p3", Unavailable("p3")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what is in Mystery"));

        response.Message.Should().Contain("not the same as it being empty");
    }

    // ─── "Why do these playlists say zero tracks?" ───────────────────────────

    [Fact]
    public async Task ReportsUnknownCountsAsUnknownWhenSummarisingTheLibrary()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylists,
            new SpotifyCommandArguments(),
            spotify => spotify.WithPlaylists(
                Owned("a", "Owned", 12),
                Unreadable("b", "Followed Thing")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("show my playlists"));

        response.Message.Should().Contain("unknown rather than zero");
    }

    [Fact]
    public async Task DescribesAPlaylistWithoutClaimingAnUnknownCountIsZero()
    {
        var service = Build(
            SpotifyReadAction.InspectPlaylist,
            new SpotifyCommandArguments(PlaylistReference: "Followed Thing"),
            spotify => spotify.WithPlaylists(Unreadable("b", "Followed Thing")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("tell me about Followed Thing"));

        response.Message.Should().Contain("unknown number of items");
        response.Message.Should().NotContain("0 items");
    }

    [Fact]
    public async Task CountsOwnedCollaborativeAndFollowedSeparately()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylists,
            new SpotifyCommandArguments(),
            spotify => spotify.WithPlaylists(
                Owned("a", "Mine", 1), Owned("b", "Mine Too", 1),
                Collaborative("c", "Ours"), Followed("d", "Theirs")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("how many playlists do I have"));

        response.Message.Should().Contain("4 playlists")
            .And.Contain("2 you own")
            .And.Contain("1 collaborative")
            .And.Contain("1 followed");
    }

    // ─── "List the Best Of playlists" ────────────────────────────────────────

    [Fact]
    public async Task FiltersByNameWithoutLosingTheRestOfTheInventory()
    {
        var service = Build(
            SpotifyReadAction.FindPlaylists,
            new SpotifyCommandArguments(Query: "Best Of"),
            spotify => spotify.WithPlaylists(
                Owned("a", "Best Of 2024", 5), Owned("b", "Best Of 2025", 5), Owned("c", "Dinner", 5)));

        var response = await service.HandleAsync(new SpotifyConversationRequest("list the Best Of playlists"));

        response.Message.Should().Contain("2 of your 3");
        ((IReadOnlyList<SpotifyPlaylistDto>)response.Data!).Should().HaveCount(2);
    }

    // ─── disambiguation ──────────────────────────────────────────────────────

    [Fact]
    public async Task AsksWhichPlaylistWhenTheNameMatchesMoreThanOne()
    {
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Chill"),
            spotify => spotify.WithPlaylists(Owned("a", "Chill", 1), Owned("b", "Chill", 2)));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what is in Chill"));

        response.Message.Should().Contain("Which one?");
        ((IReadOnlyList<SpotifyPlaylistDto>)response.Data!).Should().HaveCount(2);
    }

    [Fact]
    public async Task UsesThePlaylistTheUserPickedInsteadOfResolvingTheNameAgain()
    {
        // Having answered "which Chill?", the next turn must not re-ask.
        var service = Build(
            SpotifyReadAction.ListPlaylistItems,
            new SpotifyCommandArguments(PlaylistReference: "Chill"),
            spotify => spotify
                .WithPlaylists(Owned("a", "Chill", 1), Owned("b", "Chill", 1))
                .WithItems("b", Page(Track("Picked"))));

        var response = await service.HandleAsync(
            new SpotifyConversationRequest("what is in Chill", PlaylistId: "b"));

        response.Message.Should().NotContain("Which one?");
        response.Data.Should().BeOfType<SpotifyPlaylistItemsPageDto>()
            .Which.Items[0].Name.Should().Be("Picked");
    }

    [Fact]
    public async Task SaysSoWhenNoPlaylistMatches()
    {
        var service = Build(
            SpotifyReadAction.InspectPlaylist,
            new SpotifyCommandArguments(PlaylistReference: "Nope"),
            spotify => spotify.WithPlaylists(Owned("a", "Chill", 1)));

        var response = await service.HandleAsync(new SpotifyConversationRequest("tell me about Nope"));

        response.Message.Should().Contain("could not find");
    }

    // ─── "What is my most listened-to playlist?" ─────────────────────────────

    [Fact]
    public async Task LabelsTheRecentContextEstimateAsAnApproximation()
    {
        var service = Build(
            SpotifyReadAction.GetRecentPlaylistContexts,
            new SpotifyCommandArguments(),
            spotify => spotify
                .WithPlaylists(Owned("a", "Road Trip", 10))
                .WithRecentContexts(new SpotifyRecentPlaylistContextDto("a", null, 7, null)));

        var response = await service.HandleAsync(
            new SpotifyConversationRequest("what is my most listened to playlist"));

        response.Message.Should().Contain("Road Trip").And.Contain("approximation");
        response.Message.Should().NotContain("most listened-to playlist is");
    }

    [Fact]
    public async Task DoesNotTreatNoRecentPlaysAsEvidenceOfNotListening()
    {
        var service = Build(
            SpotifyReadAction.GetRecentPlaylistContexts,
            new SpotifyCommandArguments(),
            spotify => spotify.WithPlaylists(Owned("a", "Road Trip", 10)));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what do I listen to most"));

        response.Message.Should().Contain("not evidence");
    }

    // ─── "Can you answer questions about playlist contents?" ─────────────────

    [Fact]
    public async Task ExplainsWhatItCanAndCannotDoWithoutCallingSpotify()
    {
        var spotify = new FakeSpotify();
        var service = BuildService(
            new FakeParser(SpotifyReadAction.ExplainCapability, new SpotifyCommandArguments()), spotify);

        var response = await service.HandleAsync(new SpotifyConversationRequest("what can you do"));

        // The two facts that must survive every phase: nothing changes without a
        // confirmed plan, and Spotify cannot delete a playlist at all.
        response.Message.Should().Contain("nothing happens until you");
        response.Message.Should().Contain("no way to delete a playlist");
        spotify.PlaylistCallCount.Should().Be(0);
    }

    [Fact]
    public async Task OffersAWayForwardWhenItCannotTellWhatWasAsked()
    {
        var service = Build(SpotifyReadAction.Unknown, new SpotifyCommandArguments());

        var response = await service.HandleAsync(new SpotifyConversationRequest("mumble"));

        response.Message.Should().Contain("show my playlists");
    }

    [Fact]
    public async Task PassesThroughTheParsersOwnClarifyingQuestion()
    {
        var spotify = new FakeSpotify();
        var parser = new FakeParser(SpotifyReadAction.Unknown, new SpotifyCommandArguments(),
            clarification: "Did you mean the artist or the playlist?");
        var service = BuildService(parser, spotify);

        var response = await service.HandleAsync(new SpotifyConversationRequest("Chill"));

        response.Message.Should().Be("Did you mean the artist or the playlist?");
    }

    // ─── phase 4: analysis ───────────────────────────────────────────────────

    [Fact]
    public async Task ReportsWhatALibraryScanFoundAndThatNothingWasChanged()
    {
        var service = Build(
            SpotifyReadAction.AnalyzeLibrary,
            new SpotifyCommandArguments(),
            spotify => spotify
                .WithPlaylists(Owned("a", "Empty One", 0), Owned("b", "Has Songs", 1))
                .WithItems("a", Page())
                .WithItems("b", Page(Track("Song"))));

        var response = await service.HandleAsync(new SpotifyConversationRequest("what can I clean up"));

        response.Message.Should().Contain("1 empty playlist");
        response.Message.Should().Contain("have not changed anything");
        response.Data.Should().BeOfType<SpotifyLibraryAnalysis>();
    }

    [Fact]
    public async Task WarnsThatAScanIsPartialWhenSomePlaylistsCouldNotBeRead()
    {
        var service = Build(
            SpotifyReadAction.AnalyzeLibrary,
            new SpotifyCommandArguments(),
            spotify => spotify
                .WithPlaylists(Owned("a", "Readable", 1), Followed("b", "Hidden"))
                .WithItems("a", Page(Track("Song")))
                .WithItems("b", Forbidden("b")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("find duplicates"));

        response.Message.Should().Contain("could not be read").And.Contain("partial picture");
    }

    [Fact]
    public async Task FindsWhichPlaylistsContainASong()
    {
        var service = Build(
            SpotifyReadAction.FindItemInPlaylists,
            new SpotifyCommandArguments(Query: "Mystery Train"),
            spotify => spotify
                .WithPlaylists(Owned("a", "Blues", 1), Owned("b", "Dinner", 1))
                .WithItems("a", Page(Track("Mystery Train")))
                .WithItems("b", Page(Track("Something Else"))));

        var response = await service.HandleAsync(
            new SpotifyConversationRequest("which playlists have Mystery Train"));

        response.Message.Should().Contain("1 playlist");
        ((IReadOnlyList<SpotifyPlaylistDto>)response.Data!).Should().ContainSingle()
            .Which.Name.Should().Be("Blues");
    }

    [Fact]
    public async Task SaysASongMightStillBeInAPlaylistItCouldNotRead()
    {
        // "Not found" from a partial search is not "not there".
        var service = Build(
            SpotifyReadAction.FindItemInPlaylists,
            new SpotifyCommandArguments(Query: "Mystery Train"),
            spotify => spotify
                .WithPlaylists(Followed("b", "Hidden"))
                .WithItems("b", Forbidden("b")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("where is Mystery Train"));

        response.Message.Should().Contain("could not read").And.Contain("may also be");
    }

    [Fact]
    public async Task ComparesTwoNamedPlaylists()
    {
        var service = Build(
            SpotifyReadAction.ComparePlaylists,
            new SpotifyCommandArguments(Query: "Blues and Dinner"),
            spotify => spotify
                .WithPlaylists(Owned("a", "Blues", 2), Owned("b", "Dinner", 2))
                .WithItems("a", Page(Track("Shared", uri: "spotify:track:1"), Track("OnlyA", uri: "spotify:track:2")))
                .WithItems("b", Page(Track("Shared", uri: "spotify:track:1"), Track("OnlyB", uri: "spotify:track:3"))));

        var response = await service.HandleAsync(
            new SpotifyConversationRequest("compare Blues and Dinner"));

        response.Message.Should().Contain("share 1 song");
    }

    [Fact]
    public async Task SaysSoWhenTwoPlaylistsShareNothing()
    {
        var service = Build(
            SpotifyReadAction.ComparePlaylists,
            new SpotifyCommandArguments(Query: "Blues and Dinner"),
            spotify => spotify
                .WithPlaylists(Owned("a", "Blues", 1), Owned("b", "Dinner", 1))
                .WithItems("a", Page(Track("A", uri: "spotify:track:1")))
                .WithItems("b", Page(Track("B", uri: "spotify:track:2"))));

        var response = await service.HandleAsync(new SpotifyConversationRequest("compare Blues and Dinner"));

        response.Message.Should().Contain("no songs in common");
    }

    [Fact]
    public async Task RefusesToCompareAgainstAPlaylistItCannotRead()
    {
        var service = Build(
            SpotifyReadAction.ComparePlaylists,
            new SpotifyCommandArguments(Query: "Blues and Hidden"),
            spotify => spotify
                .WithPlaylists(Owned("a", "Blues", 1), Followed("b", "Hidden"))
                .WithItems("a", Page(Track("A")))
                .WithItems("b", Forbidden("b")));

        var response = await service.HandleAsync(new SpotifyConversationRequest("compare Blues and Hidden"));

        response.Message.Should().Contain("cannot compare");
    }

    [Theory]
    [InlineData("Rock and Roll and Dinner", "Rock and Roll", "Dinner")]
    [InlineData("Blues and Dinner", "Blues", "Dinner")]
    public void SplitsAPairOnTheLastAndSoPlaylistNamesContainingAndSurvive(
        string query, string expectedLeft, string expectedRight)
    {
        var pair = SpotifyConversationService.SplitPair(query);

        pair!.Value.Left.Should().Be(expectedLeft);
        pair.Value.Right.Should().Be(expectedRight);
    }

    [Theory]
    [InlineData("just one name")]
    [InlineData("and trailing")]
    [InlineData("leading and")]
    public void RefusesAPairItCannotSplit(string query)
    {
        SpotifyConversationService.SplitPair(query).Should().BeNull();
    }

    [Fact]
    public async Task DescribesTopItemsWithTheWindowTheyCoverRatherThanASpotifyTermOfArt()
    {
        var service = Build(
            SpotifyReadAction.GetTopItems,
            new SpotifyCommandArguments(Query: "artists", TimeRange: "long_term"),
            spotify => spotify.WithTopItems(new SpotifyTopItemsDto("artists", "long_term",
                [new SpotifyTopItemDto("a", "Skip James", "delta blues", null, 1)])));

        var response = await service.HandleAsync(new SpotifyConversationRequest("who do I listen to most"));

        response.Message.Should().Contain("several years");
        response.Message.Should().NotContain("long_term");
    }

    // ─── the policy boundary ─────────────────────────────────────────────────

    [Fact]
    public async Task NeverHandsSpotifyContentToTheParser()
    {
        // Spotify's policy forbids feeding their content into AI systems, and this
        // is the line that enforces it: the parser sees the user's message and
        // nothing else. It forwards any context it is given (pinned in its own
        // tests), so passing transcript here is what would leak — hence null.
        var parser = new RecordingParser();
        var spotify = new FakeSpotify().WithPlaylists(Owned("secret-id", "Lucy + Laura", 3));
        var service = BuildService(parser, spotify);

        await service.HandleAsync(new SpotifyConversationRequest("show my playlists"));

        parser.SeenContext.Should().BeNull();
        parser.SeenMessage.Should().Be("show my playlists");
    }

    private sealed class RecordingParser : ISpotifyCommandParser
    {
        public string? SeenMessage { get; private set; }
        public string? SeenContext { get; private set; }

        public Task<SpotifyValidatedCommand> ParseAsync(
            string message, string? conversationContext = null, string? billTo = null,
            CancellationToken token = default)
        {
            SeenMessage = message;
            SeenContext = conversationContext;
            return Task.FromResult(new SpotifyValidatedCommand(
                SpotifyReadAction.ListPlaylists, new SpotifyCommandArguments(), 1.0));
        }
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    private static SpotifyConversationService Build(
        SpotifyReadAction action,
        SpotifyCommandArguments arguments,
        Action<FakeSpotify>? configure = null)
    {
        var spotify = new FakeSpotify();
        configure?.Invoke(spotify);
        return BuildService(new FakeParser(action, arguments), spotify);
    }

    private static SpotifyPlaylistDto Owned(string id, string name, int? count) =>
        new(id, name, null, count, null, ContentsAvailable: true, IsOwnedByUser: true, IsPublic: false);

    private static SpotifyPlaylistDto Unreadable(string id, string name) =>
        new(id, name, null, null, null, ContentsAvailable: false);

    private static SpotifyPlaylistDto Collaborative(string id, string name) =>
        new(id, name, null, 0, null, IsOwnedByUser: false, IsCollaborative: true);

    private static SpotifyPlaylistDto Followed(string id, string name) =>
        new(id, name, null, 0, null, IsOwnedByUser: false);

    private static SpotifyPlaylistItemDto Track(string name, string? uri = null) =>
        new(0, SpotifyItemKind.Track, "t", name, uri ?? $"spotify:track:{name}", "Artist",
            "Album", 1000, null, false, null);

    private static SpotifyPlaylistItemsPageDto Page(params SpotifyPlaylistItemDto[] items) =>
        new("p", items, items.Length, 0, 50, false);

    private static SpotifyPlaylistItemsPageDto Forbidden(string id) =>
        new(id, [], 0, 0, 50, false, SpotifyContentsAccess.Forbidden);

    private static SpotifyPlaylistItemsPageDto Unavailable(string id) =>
        new(id, [], 0, 0, 50, false, SpotifyContentsAccess.Unavailable);

    /// <summary>
    /// Builds the service with every dependency supplied. The constructor takes all
    /// eight because a shorter one let a missing DI registration pass silently; these
    /// tests only care about three of them, so the rest are Moq defaults.
    /// </summary>
    private static SpotifyConversationService BuildService(
        ISpotifyCommandParser parser, ISpotifyService spotify) =>
        new(parser,
            spotify,
            new CachedInventory(spotify),
            FreshInventoryJobs(),
            Mock.Of<ISpotifyCurrentUser>(x => x.GetRequiredOwnerKey() == "paul"),
            new KnownMusicFromSpotify(spotify),
            Mock.Of<ISpotifyDiscoveryService>(),
            Mock.Of<ISpotifyPlanService>());

    /// <summary>
    /// Reports a completed inventory from a moment ago, so analysis proceeds on cached
    /// contents instead of queueing a scan. These tests are about what the analysis
    /// *says*, not about the job that feeds it.
    /// </summary>
    private static ISpotifyInventoryJobService FreshInventoryJobs()
    {
        var status = new SpotifyInventoryStatusDto(
            null, SpotifyInventoryJobState.Complete, 0, 0, 0, 0, 0,
            null, null, null, DateTimeOffset.UtcNow);

        var jobs = new Mock<ISpotifyInventoryJobService>();
        jobs.Setup(j => j.GetStatus(It.IsAny<string>())).Returns(status);
        jobs.Setup(j => j.Start(It.IsAny<string>())).Returns(status);
        return jobs.Object;
    }

    /// <summary>
    /// The real in-memory inventory, plus a cache that actually holds something.
    ///
    /// <c>AnalyzeLibraryAsync</c> reads the *cached* library — production always has a
    /// SQLite-backed store behind it — but the one-argument test harness has no store,
    /// so <c>LoadCachedLibrary</c> would return nothing and every analysis assertion
    /// would be about an empty library. This serves the cache from the same fake the
    /// rest of the test uses.
    /// </summary>
    private sealed class CachedInventory(ISpotifyService spotify) : ISpotifyInventoryService
    {
        private readonly SpotifyInventoryService _inner = new(spotify);

        public Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync(
            bool forceRefresh = false, CancellationToken token = default) =>
            _inner.GetPlaylistsAsync(forceRefresh, token);

        public Task<SpotifyPlaylistContents> GetContentsAsync(
            SpotifyPlaylistDto playlist, CancellationToken token = default) =>
            _inner.GetContentsAsync(playlist, token);

        public Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
            IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default) =>
            _inner.GetAllContentsAsync(playlists, token);

        public Task<IReadOnlyList<SpotifyPlaylistContents>> RefreshForOwnerAsync(
            string ownerKey,
            Action<int, int, SpotifyPlaylistContents>? progress = null,
            CancellationToken token = default) =>
            _inner.RefreshForOwnerAsync(ownerKey, progress, token);

        public IReadOnlyList<SpotifyPlaylistContents> LoadCachedLibrary(string ownerKey)
        {
            var playlists = _inner.GetPlaylistsAsync().GetAwaiter().GetResult();
            return _inner.GetAllContentsAsync(playlists).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Serves recent contexts and top items straight from the fake Spotify.
    ///
    /// The conversation service used to fall back to <c>ISpotifyService</c> when no
    /// known-music service was injected, and these tests relied on that fallback. The
    /// fallback is gone — every dependency is required now — so the double does the
    /// same delegation explicitly, keeping the tests about what the *response says*
    /// rather than about which collaborator produced the numbers.
    /// </summary>
    private sealed class KnownMusicFromSpotify(ISpotifyService spotify) : ISpotifyKnownMusicService
    {
        public Task<SpotifyKnownMusicReport> GetAsync(CancellationToken token = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind, string window, int limit = 20, CancellationToken token = default) =>
            spotify.GetTopItemsAsync(kind, window, limit, token);

        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentContextsAsync(
            CancellationToken token = default) =>
            spotify.GetRecentPlaylistContextsAsync(token);

        public SpotifyKnownMusicOverrideResult ApplyOverride(SpotifyKnownMusicOverrideRequest request) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeParser(
        SpotifyReadAction action, SpotifyCommandArguments arguments, string? clarification = null)
        : ISpotifyCommandParser
    {
        public Task<SpotifyValidatedCommand> ParseAsync(
            string message, string? conversationContext = null, string? billTo = null,
            CancellationToken token = default) =>
            Task.FromResult(new SpotifyValidatedCommand(action, arguments, 1.0, clarification));
    }

    private sealed class FakeSpotify : ISpotifyService
    {
        private readonly List<SpotifyPlaylistDto> _playlists = [];
        private readonly Dictionary<string, SpotifyPlaylistItemsPageDto> _items = [];
        private readonly List<SpotifyRecentPlaylistContextDto> _contexts = [];

        public int PlaylistCallCount { get; private set; }

        public FakeSpotify WithPlaylists(params SpotifyPlaylistDto[] playlists)
        {
            _playlists.AddRange(playlists);
            return this;
        }

        public FakeSpotify WithItems(string playlistId, SpotifyPlaylistItemsPageDto page)
        {
            _items[playlistId] = page;
            return this;
        }

        public FakeSpotify WithRecentContexts(params SpotifyRecentPlaylistContextDto[] contexts)
        {
            _contexts.AddRange(contexts);
            return this;
        }

        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default)
        {
            PlaylistCallCount++;
            return Task.FromResult(_playlists.ToList());
        }

        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(_items.TryGetValue(playlistId, out var page)
                ? page
                : new SpotifyPlaylistItemsPageDto(playlistId, [], 0, offset, limit, false));

        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
            CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>(_contexts);

        public SpotifyTopItemsDto? TopItems { get; set; }

        public FakeSpotify WithTopItems(SpotifyTopItemsDto top)
        {
            TopItems = top;
            return this;
        }

        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) =>
            Task.FromResult(TopItems ?? new SpotifyTopItemsDto(kind, timeRange, []));

        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) =>
            Task.FromResult(_playlists.FirstOrDefault(p => p.Id == playlistId));

        public Task<SpotifySearchResultDto> SearchTracksAsync(
            string query, int limit = 10, CancellationToken token = default) =>
            Task.FromResult(new SpotifySearchResultDto([], 0));

        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(
            string name, string? description = null, bool isPublic = false, CancellationToken token = default) =>
            throw new InvalidOperationException("Writes are not part of the read-only inspector.");

        public Task AddTracksToPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new InvalidOperationException("Writes are not part of the read-only inspector.");

        public Task RemoveTracksFromPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new InvalidOperationException("Writes are not part of the read-only inspector.");

        public Task RemovePlaylistsFromLibraryAsync(
            List<string> playlistUris, CancellationToken token = default) =>
            throw new InvalidOperationException("Writes are not part of the read-only inspector.");

        public Task<string?> AddItemsAsync(
            string playlistId, IReadOnlyList<string> uris, CancellationToken token = default) =>
            Task.FromResult<string?>("snapshot");

        public Task<string?> RemoveItemsAsync(
            string playlistId, IReadOnlyList<string> uris, string? snapshotId = null,
            CancellationToken token = default) => Task.FromResult<string?>("snapshot");

        public Task<string?> ReplaceItemsAsync(
            string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default) =>
            Task.FromResult<string?>("snapshot");

        public Task<string?> ReorderItemsAsync(
            string playlistId, int rangeStart, int insertBefore, int rangeLength,
            string? snapshotId = null, CancellationToken token = default) =>
            Task.FromResult<string?>("snapshot");

        public Task ChangePlaylistDetailsAsync(
            string playlistId, string? name = null, string? description = null, bool? isPublic = null,
            CancellationToken token = default) => Task.CompletedTask;

    }
}
