using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Executing a bulk plan (phase 8).
///
/// One promise dominates everything here: a merge never lets go of the originals
/// until it has checked, by reading Spotify back, that the copy actually landed.
/// Every other guarantee in this file exists to make that one hold under failure —
/// resumption that does not re-run finished work, adds that cannot duplicate, and a
/// removal that records how to undo itself before it happens.
/// </summary>
public class SpotifyBulkExecutionTests
{
    private const string Owner = "owner-key";
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── the gate that protects the originals ────────────────────────────────

    [Fact]
    public async Task RemovesTheSourcesOnceTheMergedPlaylistChecksOut()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");

        var executed = await Run(executor, MergePlan(expected: 2));

        executed.Status.Should().Be(SpotifyPlanStatus.Completed);
        spotify.LibraryRemovals.Should().Equal("spotify:playlist:p1", "spotify:playlist:p2");
    }

    [Fact]
    public async Task NeverRemovesASourceWhenTheMergedPlaylistIsShort()
    {
        // The disaster case. The add reported success but only one of two tracks is
        // actually there, so the originals must stay exactly where they are.
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a");

        var executed = await Run(executor, MergePlan(expected: 2));

        spotify.LibraryRemovals.Should().BeEmpty();
        executed.OrderedSteps
            .Where(s => s.Kind == SpotifyPlanStepKind.RemoveFromLibrary)
            .Should().OnlyContain(s => s.Status == SpotifyPlanStepStatus.Skipped);
    }

    [Fact]
    public async Task SaysWhyItStoppedAndThatNothingElseWasTouched()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a");

        var executed = await Run(executor, MergePlan(expected: 2));

        executed.Status.Should().Be(SpotifyPlanStatus.PartiallyCompleted);
        executed.Failure.Should().Contain("has 1 items but should have 2");
        executed.Failure.Should().Contain("original playlists have not been touched");
    }

    [Fact]
    public async Task RefusesToConfirmTheMergeLandedWhenSpotifyWillNotShowItBack()
    {
        // "I could not check" must not be treated the same as "I checked and it was
        // fine". An unreadable read-back stops the plan too.
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");
        spotify.ContentsReadable = false;

        var executed = await Run(executor, MergePlan(expected: 2));

        spotify.LibraryRemovals.Should().BeEmpty();
        executed.Failure.Should().Contain("would not let me read");
    }

    [Fact]
    public async Task AVerifiedMergeWithMoreThanExpectedStillPasses()
    {
        // The user added a song themselves between the plan and its execution. The
        // check is "did everything arrive", not "is this exactly what I predicted".
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b", "surprise");

        var executed = await Run(executor, MergePlan(expected: 2));

        executed.Status.Should().Be(SpotifyPlanStatus.Completed);
        spotify.LibraryRemovals.Should().HaveCount(2);
    }

    // ─── removing from the library ───────────────────────────────────────────

    [Fact]
    public async Task RemovalPassesThePlaylistUriToSpotify()
    {
        var (executor, spotify, _) = Build();

        await Run(executor, RemovalPlan());

        spotify.LibraryRemovals.Should().Equal("spotify:playlist:p1");
    }

    [Fact]
    public async Task RemovalRecordsHowToPutItBackBeforeItHappens()
    {
        var (executor, _, store) = Build();

        var executed = await Run(executor, RemovalPlan());

        var manifest = executed.RestoreManifests!.Single();
        manifest.RemovedLibraryUri.Should().Be("spotify:playlist:p1");
        store.Get(Owner, executed.Id)!.CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task UndoingARemovalFollowsThePlaylistAgain()
    {
        var (executor, _, _) = Build();
        var executed = await Run(executor, RemovalPlan());

        var undo = await executor.BuildUndoAsync(Owner, executed.Id);

        var step = undo.OrderedSteps.Single();
        step.Kind.Should().Be(SpotifyPlanStepKind.AddToLibrary);
        step.Uris.Should().Equal("spotify:playlist:p1");
    }

    [Fact]
    public async Task TheRestoreActuallyCallsSpotifyToRefollow()
    {
        var (executor, spotify, store) = Build();
        var executed = await Run(executor, RemovalPlan());
        var undo = await executor.BuildUndoAsync(Owner, executed.Id);

        store.Save(Owner, undo with { Status = SpotifyPlanStatus.AwaitingConfirmation });
        await executor.ConfirmAndExecuteAsync(Owner, undo.Id, "Paul", highImpactAcknowledged: true);

        spotify.LibraryAdditions.Should().Equal("spotify:playlist:p1");
    }

    // ─── undoing a creation ──────────────────────────────────────────────────

    [Fact]
    public async Task UndoingACreationTakesTheNewPlaylistBackOutOfTheLibrary()
    {
        // Spotify has no delete, so the inverse of "create" is "unfollow". Without
        // this, an unwanted playlist has to be found and removed by hand.
        var (executor, _, _) = Build();
        var executed = await Run(executor, CreatePlan());

        var undo = await executor.BuildUndoAsync(Owner, executed.Id);

        var step = undo.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.RemoveFromLibrary);
        step.Uris.Should().Equal("spotify:playlist:merged");
        undo.Preview!.Warnings.Should().Contain(w => w.Contains("not deleted, only unfollowed"));
    }

    // ─── adds cannot duplicate ───────────────────────────────────────────────

    [Fact]
    public async Task DoesNotAddATrackThePlaylistAlreadyHas()
    {
        // What makes re-running a half-finished add safe. Without it, an add that
        // landed but timed out on the response would double every track on retry.
        var (executor, spotify, _) = Build();
        spotify.SetContents("p1", "a");

        await Run(executor, AddPlan("p1", "a", "b"));

        spotify.AddedUris.Should().Equal("spotify:track:b");
    }

    [Fact]
    public async Task AddsEverythingWhenSpotifyWillNotShowWhatIsAlreadyThere()
    {
        // Skipping an add needs an authoritative read. Without one, adding the whole
        // list is the safer error — a duplicate is visible and removable; a missing
        // track is neither.
        var (executor, spotify, _) = Build();
        spotify.SetContents("p1", "a");
        spotify.ContentsReadable = false;

        await Run(executor, AddPlan("p1", "a", "b"));

        spotify.AddedUris.Should().Equal("spotify:track:a", "spotify:track:b");
    }

    // ─── picking a stalled plan back up ──────────────────────────────────────

    [Fact]
    public async Task ResumingRunsOnlyTheStepsThatDidNotFinish()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");
        spotify.FailLibraryRemoval = true;

        var stalled = await Run(executor, MergePlan(expected: 2));
        stalled.Status.Should().Be(SpotifyPlanStatus.PartiallyCompleted);
        var addsBefore = spotify.AddCalls;

        spotify.FailLibraryRemoval = false;
        var resumed = await executor.ResumeAsync(Owner, stalled.Id, "Paul");

        resumed.Status.Should().Be(SpotifyPlanStatus.Completed);
        spotify.AddCalls.Should().Be(addsBefore, "the add already succeeded and must not run again");
        spotify.CreateCalls.Should().Be(1, "the playlist already exists");
    }

    [Fact]
    public async Task ResumingFinishesTheWorkThatWasSkipped()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");
        spotify.FailLibraryRemoval = true;

        var stalled = await Run(executor, MergePlan(expected: 2));

        spotify.FailLibraryRemoval = false;
        await executor.ResumeAsync(Owner, stalled.Id, "Paul");

        spotify.LibraryRemovals.Should().Equal("spotify:playlist:p1", "spotify:playlist:p2");
    }

    [Fact]
    public async Task RefusesToResumeAPlanThatFinished()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");
        var executed = await Run(executor, MergePlan(expected: 2));

        var act = () => executor.ResumeAsync(Owner, executed.Id, "Paul");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("stopped part-way");
    }

    [Fact]
    public async Task ResumingReportsItsOwnFailureRatherThanTheOldOne()
    {
        var (executor, spotify, _) = Build();
        spotify.SetContents("merged", "a", "b");
        spotify.FailLibraryRemoval = true;

        var stalled = await Run(executor, MergePlan(expected: 2));
        var resumed = await executor.ResumeAsync(Owner, stalled.Id, "Paul");

        // Still failing, but the text must describe this attempt.
        resumed.Status.Should().Be(SpotifyPlanStatus.PartiallyCompleted);
        resumed.OrderedSteps
            .Where(s => s.Status == SpotifyPlanStepStatus.Succeeded)
            .Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task TheResumptionIsRecordedInTheAudit()
    {
        var (executor, spotify, _, audit) = BuildWithAudit();
        spotify.SetContents("merged", "a", "b");
        spotify.FailLibraryRemoval = true;

        var stalled = await Run(executor, MergePlan(expected: 2));
        spotify.FailLibraryRemoval = false;
        await executor.ResumeAsync(Owner, stalled.Id, "Paul");

        audit.List(Owner, stalled.Id).Select(e => e.Kind)
            .Should().Contain(SpotifyAuditEventKind.PlanResumed);
    }

    // ─── progress is visible while it runs ───────────────────────────────────

    [Fact]
    public async Task RecordsProgressAfterEveryStepRatherThanOnlyAtTheEnd()
    {
        // A bulk plan is long enough to watch, and a process that dies mid-run must
        // leave a record of what it had already done.
        var (executor, spotify, store) = Build();
        spotify.SetContents("merged", "a", "b");

        await Run(executor, MergePlan(expected: 2));

        // One save per step, plus the confirmation and completion saves.
        store.Saves.Should().BeGreaterThan(MergePlan(expected: 2).OrderedSteps.Count);
    }

    [Fact]
    public async Task AStepThatSucceededIsPersistedBeforeALaterOneFails()
    {
        var (executor, spotify, store) = Build();
        spotify.SetContents("merged", "a", "b");
        spotify.FailLibraryRemoval = true;

        var stalled = await Run(executor, MergePlan(expected: 2));

        var stored = store.Get(Owner, stalled.Id)!;
        stored.OrderedSteps.Count(s => s.Status == SpotifyPlanStepStatus.Succeeded).Should().Be(3);
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    /// <summary>Stores the plan, then confirms and runs it — the whole live path.</summary>
    private ExecutorHarness _harness = null!;

    private async Task<SpotifyChangePlan> Run(ISpotifyPlanExecutor executor, SpotifyChangePlan plan)
    {
        _harness.Store.Save(Owner, plan);
        return await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", highImpactAcknowledged: true);
    }

    private (ISpotifyPlanExecutor, FakeSpotify, CountingPlanStore) Build()
    {
        var (executor, spotify, store, _) = BuildWithAudit();
        return (executor, spotify, store);
    }

    private (ISpotifyPlanExecutor, FakeSpotify, CountingPlanStore, InMemoryAudit) BuildWithAudit()
    {
        var spotify = new FakeSpotify();
        var store = new CountingPlanStore();
        var audit = new InMemoryAudit();
        var executor = new SpotifyPlanExecutor(
            spotify, new FakeInventory(spotify), store, audit, new StubTokens(), new StubClock(Now));

        _harness = new ExecutorHarness(store);
        return (executor, spotify, store, audit);
    }

    private sealed record ExecutorHarness(CountingPlanStore Store);

    /// <summary>Create → add two tracks → verify → unfollow both sources.</summary>
    private SpotifyChangePlan MergePlan(int expected)
    {
        var plan = SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.MergePlaylists,
            [new SpotifyPlanTarget("p1", "A", null), new SpotifyPlanTarget("p2", "B", null)], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps =
            [
                new SpotifyPlanStep(0, SpotifyPlanStepKind.CreatePlaylist, null, "Merged", Name: "Merged"),
                new SpotifyPlanStep(1, SpotifyPlanStepKind.AddItems, null, "Merged",
                    Uris: ["spotify:track:a", "spotify:track:b"]),
                new SpotifyPlanStep(2, SpotifyPlanStepKind.VerifyPlaylistPopulated, null, "Merged",
                    ExpectedItemCount: expected),
                new SpotifyPlanStep(3, SpotifyPlanStepKind.RemoveFromLibrary, "p1", "A",
                    Uris: ["spotify:playlist:p1"]),
                new SpotifyPlanStep(4, SpotifyPlanStepKind.RemoveFromLibrary, "p2", "B",
                    Uris: ["spotify:playlist:p2"])
            ],
            Preview = new SpotifyPlanPreview("Merge", "Merge", [], [], RequiresHighImpactAcknowledgement: true)
        };

        return plan;
    }

    private SpotifyChangePlan RemovalPlan()
    {
        var plan = SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.RemovePlaylistsFromLibrary,
            [new SpotifyPlanTarget("p1", "Old Mix", null)], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps =
            [
                new SpotifyPlanStep(0, SpotifyPlanStepKind.RemoveFromLibrary, "p1", "Old Mix",
                    Uris: ["spotify:playlist:p1"])
            ],
            Preview = new SpotifyPlanPreview("Remove", "Remove", [], [], RequiresHighImpactAcknowledgement: true)
        };

        return plan;
    }

    private SpotifyChangePlan CreatePlan()
    {
        var plan = SpotifyPlanStateMachine.Create(SpotifyPlanAction.CreatePlaylist, [], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps = [new SpotifyPlanStep(0, SpotifyPlanStepKind.CreatePlaylist, null, "Merged", Name: "Merged")],
            Preview = new SpotifyPlanPreview("Create", "Create", [], [], false)
        };

        return plan;
    }

    private SpotifyChangePlan AddPlan(string playlistId, params string[] trackIds)
    {
        var plan = SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.AddItems, [new SpotifyPlanTarget(playlistId, "Mine", null)], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps =
            [
                new SpotifyPlanStep(0, SpotifyPlanStepKind.AddItems, playlistId, "Mine",
                    Uris: trackIds.Select(id => $"spotify:track:{id}").ToList())
            ],
            Preview = new SpotifyPlanPreview("Add", "Add", [], [], false)
        };

        return plan;
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Counts writes so "progress is persisted as it goes" is testable.</summary>
    private sealed class CountingPlanStore : ISpotifyPlanStore
    {
        private readonly Dictionary<(string, Guid), SpotifyChangePlan> _plans = [];

        public int Saves { get; private set; }

        public SpotifyChangePlan? Get(string ownerKey, Guid planId) =>
            _plans.TryGetValue((ownerKey, planId), out var plan) ? plan : null;

        public IReadOnlyList<SpotifyChangePlan> List(string ownerKey, int limit = 50) =>
            _plans.Where(kv => kv.Key.Item1 == ownerKey).Select(kv => kv.Value).ToList();

        public void Save(string ownerKey, SpotifyChangePlan plan)
        {
            Saves++;
            _plans[(ownerKey, plan.Id)] = plan;
        }
    }

    private sealed class InMemoryAudit : ISpotifyAuditService
    {
        private readonly List<(string Owner, SpotifyAuditEvent Event)> _events = [];

        public void Record(string ownerKey, SpotifyAuditEvent auditEvent) => _events.Add((ownerKey, auditEvent));

        public IReadOnlyList<SpotifyAuditEvent> List(string ownerKey, Guid? planId = null, int limit = 100) =>
            _events.Where(e => e.Owner == ownerKey && (planId is null || e.Event.PlanId == planId))
                   .Select(e => e.Event).ToList();
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

    private sealed class FakeInventory(FakeSpotify spotify) : ISpotifyInventoryService
    {
        public Task<SpotifyPlaylistContents> GetContentsAsync(
            SpotifyPlaylistDto playlist, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistContents(
                playlist,
                spotify.ItemsFor(playlist.Id),
                spotify.ContentsReadable ? SpotifyContentsAccess.Available : SpotifyContentsAccess.Forbidden,
                playlist.SnapshotId));

        public Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
            IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyPlaylistContents>>([]);

        public Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync(
            bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyPlaylistDto>>([]);

        public Task<IReadOnlyList<SpotifyPlaylistContents>> RefreshForOwnerAsync(
            string ownerKey, Action<int, int, SpotifyPlaylistContents>? progress = null,
            CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyPlaylistContents>>([]);

        public IReadOnlyList<SpotifyPlaylistContents> LoadCachedLibrary(string ownerKey) => [];
    }

    private sealed class FakeSpotify : ISpotifyService
    {
        private readonly Dictionary<string, List<SpotifyPlaylistItemDto>> _contents = [];

        public List<string> LibraryRemovals { get; } = [];
        public List<string> LibraryAdditions { get; } = [];
        public List<string> AddedUris { get; } = [];
        public int AddCalls;
        public int CreateCalls;
        public bool FailLibraryRemoval;
        public bool ContentsReadable = true;

        public void SetContents(string playlistId, params string[] trackIds) =>
            _contents[playlistId] = trackIds
                .Select((id, index) => new SpotifyPlaylistItemDto(
                    index, SpotifyItemKind.Track, id, "Track " + id, $"spotify:track:{id}",
                    "Artist", "Album", 1000, null, false, null))
                .ToList();

        public IReadOnlyList<SpotifyPlaylistItemDto> ItemsFor(string playlistId) =>
            _contents.TryGetValue(playlistId, out var items) ? items : [];

        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) =>
            Task.FromResult<SpotifyPlaylistDto?>(new SpotifyPlaylistDto(
                playlistId, playlistId == "merged" ? "Merged" : "Mine", null,
                ItemsFor(playlistId).Count, null, IsOwnedByUser: true, SnapshotId: null));

        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(
            string name, string? description = null, bool isPublic = false, CancellationToken token = default)
        {
            CreateCalls++;
            return Task.FromResult(new SpotifyPlaylistDto(
                "merged", name, null, 0, null, IsOwnedByUser: true, SnapshotId: null));
        }

        public Task<string?> AddItemsAsync(
            string playlistId, IReadOnlyList<string> uris, CancellationToken token = default)
        {
            AddCalls++;
            AddedUris.AddRange(uris);
            return Task.FromResult<string?>("snap-after");
        }

        public Task RemovePlaylistsFromLibraryAsync(
            List<string> playlistUris, CancellationToken token = default)
        {
            if (FailLibraryRemoval)
                throw new SpotifyApiException(System.Net.HttpStatusCode.InternalServerError, "denied", null, null);

            LibraryRemovals.AddRange(playlistUris);
            return Task.CompletedTask;
        }

        public Task AddPlaylistsToLibraryAsync(
            List<string> playlistUris, CancellationToken token = default)
        {
            LibraryAdditions.AddRange(playlistUris);
            return Task.CompletedTask;
        }

        public Task<string?> ReplaceItemsAsync(
            string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default) =>
            Task.FromResult<string?>("snap-after");

        public Task<string?> RemoveItemsAsync(
            string playlistId, IReadOnlyList<string> uris, string? snapshotId = null,
            CancellationToken token = default) => Task.FromResult<string?>("snap-after");

        public Task<string?> ReorderItemsAsync(
            string playlistId, int rangeStart, int insertBefore, int rangeLength,
            string? snapshotId = null, CancellationToken token = default) =>
            Task.FromResult<string?>("snap-after");

        public Task ChangePlaylistDetailsAsync(
            string playlistId, string? name = null, string? description = null, bool? isPublic = null,
            CancellationToken token = default) => Task.CompletedTask;

        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) =>
            Task.FromResult(new List<SpotifyPlaylistDto>());
        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistItemsPageDto(
                playlistId, ItemsFor(playlistId), ItemsFor(playlistId).Count, 0, 50, false));
        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
            CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>([]);
        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) => Task.FromResult(new SpotifyTopItemsDto(kind, timeRange, []));
        public Task<SpotifySearchResultDto> SearchTracksAsync(
            string query, int limit = 10, CancellationToken token = default) =>
            Task.FromResult(new SpotifySearchResultDto([], 0));
        public Task AddTracksToPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) => Task.CompletedTask;
        public Task RemoveTracksFromPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) => Task.CompletedTask;
    }
}
