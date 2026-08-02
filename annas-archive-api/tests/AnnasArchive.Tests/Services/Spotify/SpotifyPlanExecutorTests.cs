using System.Net;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The executor is the only thing in the system that writes to Spotify. These tests
/// are about the four promises that makes it safe: it runs once, it stops at the
/// first failure, it refuses to act on a playlist that moved since review, and it
/// records a way back before destroying anything.
/// </summary>
public class SpotifyPlanExecutorTests
{
    private const string Owner = "owner-key";
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    // ─── it runs once ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutesAConfirmedPlan()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, AddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.Status.Should().Be(SpotifyPlanStatus.Completed);
        spotify.AddCalls.Should().Be(1);
        executed.OrderedSteps[0].Status.Should().Be(SpotifyPlanStepStatus.Succeeded);
    }

    [Fact]
    public async Task RecordsTheSnapshotSpotifyReturned()
    {
        var (executor, _, store) = Build();
        var plan = Save(store, AddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.OrderedSteps[0].ResultingSnapshotId.Should().Be("snap-after");
    }

    [Fact]
    public async Task DoesNotWriteTwiceWhenConfirmedTwice()
    {
        // The double-tap case. Executing again must be a no-op that reports the
        // existing outcome, not a second set of writes.
        var (executor, spotify, store) = Build();
        var plan = Save(store, AddPlan());

        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);
        var second = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        spotify.AddCalls.Should().Be(1);
        second.Status.Should().Be(SpotifyPlanStatus.Completed);
    }

    [Fact]
    public async Task RefusesToRunACancelledPlan()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, AddPlan() with { Status = SpotifyPlanStatus.Cancelled });

        var act = () => executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        spotify.AddCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefusesAPlanThatHasExpired()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, AddPlan() with { ExpiresAtUtc = Now.AddMinutes(-1) });

        var act = () => executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("expired");
        spotify.AddCalls.Should().Be(0);
    }

    // ─── high-impact gate ────────────────────────────────────────────────────

    [Fact]
    public async Task RefusesAHighImpactPlanWithoutTheSecondAcknowledgement()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, ReplacePlan());

        var act = () => executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", highImpactAcknowledged: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        spotify.ReplaceCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunsAHighImpactPlanOnceAcknowledged()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, ReplacePlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", highImpactAcknowledged: true);

        executed.Status.Should().Be(SpotifyPlanStatus.Completed);
        spotify.ReplaceCalls.Should().Be(1);
    }

    // ─── snapshot drift ──────────────────────────────────────────────────────

    [Fact]
    public async Task RefusesWhenThePlaylistChangedSinceThePlanWasReviewed()
    {
        // The preview the user approved described a different playlist than the one
        // that exists now, so applying it would apply something unreviewed.
        var (executor, spotify, store) = Build();
        spotify.CurrentSnapshot = "snapshot-moved-on";
        var plan = Save(store, AddPlan());

        var act = () => executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("changed since");
        spotify.AddCalls.Should().Be(0);
    }

    [Fact]
    public async Task MarksAPlanExpiredWhenItsTargetDrifted()
    {
        var (executor, spotify, store) = Build();
        spotify.CurrentSnapshot = "snapshot-moved-on";
        var plan = Save(store, AddPlan());

        try { await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false); }
        catch (InvalidOperationException) { /* expected */ }

        store.Get(Owner, plan.Id)!.Status.Should().Be(SpotifyPlanStatus.Expired);
    }

    [Fact]
    public async Task CancelsThePlanWhenItsTargetNoLongerExists()
    {
        var (executor, spotify, store) = Build();
        spotify.PlaylistExists = false;
        var plan = Save(store, AddPlan());

        try { await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false); }
        catch (InvalidOperationException) { /* expected */ }

        store.Get(Owner, plan.Id)!.Status.Should().Be(SpotifyPlanStatus.Cancelled);
    }

    // ─── stop at the first failure ───────────────────────────────────────────

    [Fact]
    public async Task SkipsEveryLaterStepOnceOneFails()
    {
        // This is what stops a merge removing its sources when the target never
        // populated. Needs three steps: with the failure last there is nothing left
        // to skip, and the test would pass even without the guard.
        var (executor, spotify, store) = Build();
        spotify.FailOnAddCall = 1;
        var plan = Save(store, ThreeStepPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        var steps = executed.OrderedSteps;
        steps[0].Status.Should().Be(SpotifyPlanStepStatus.Succeeded);
        steps[1].Status.Should().Be(SpotifyPlanStepStatus.Failed);
        steps[2].Status.Should().Be(SpotifyPlanStepStatus.Skipped);
        executed.Status.Should().Be(SpotifyPlanStatus.PartiallyCompleted);
    }

    [Fact]
    public async Task NeverCallsSpotifyForAStepAfterAFailure()
    {
        // The status alone could be cosmetic; this asserts the write never happened.
        var (executor, spotify, store) = Build();
        spotify.FailOnAddCall = 1;
        var plan = Save(store, ThreeStepPlan());

        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        spotify.AddCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReportsPartialCompletionRatherThanPlainFailureWhenSomethingLanded()
    {
        var (executor, spotify, store) = Build();
        spotify.FailAddItems = true;
        var plan = Save(store, CreateThenAddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.Status.Should().Be(SpotifyPlanStatus.PartiallyCompleted);
        executed.Failure.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReportsOutrightFailureWhenNothingLanded()
    {
        var (executor, spotify, store) = Build();
        spotify.FailAddItems = true;
        var plan = Save(store, AddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.Status.Should().Be(SpotifyPlanStatus.Failed);
    }

    [Fact]
    public async Task TranslatesASpotifyForbiddenIntoSomethingAPersonCanActOn()
    {
        var (executor, spotify, store) = Build();
        spotify.FailAddItems = true;
        spotify.FailureStatus = HttpStatusCode.Forbidden;
        var plan = Save(store, AddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.Failure.Should().Contain("not yours to modify");
    }

    // ─── the created playlist flows into later steps ─────────────────────────

    [Fact]
    public async Task GivesLaterStepsTheIdOfThePlaylistItJustCreated()
    {
        var (executor, spotify, store) = Build();
        var plan = Save(store, CreateThenAddPlan());

        var executed = await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        executed.OrderedSteps[0].CreatedPlaylistId.Should().Be("new-playlist");
        executed.OrderedSteps[1].PlaylistId.Should().Be("new-playlist");
        spotify.LastAddPlaylistId.Should().Be("new-playlist");
    }

    // ─── audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WritesAnAuditTrailOfConfirmationAndOutcome()
    {
        var (executor, _, store, audit) = BuildWithAudit();
        var plan = Save(store, AddPlan());

        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        var kinds = audit.List(Owner, plan.Id).Select(e => e.Kind).ToList();
        kinds.Should().Contain(SpotifyAuditEventKind.PlanConfirmed);
        kinds.Should().Contain(SpotifyAuditEventKind.PlanCompleted);
    }

    [Fact]
    public async Task RecordsWhoConfirmed()
    {
        var (executor, _, store, audit) = BuildWithAudit();
        var plan = Save(store, AddPlan());

        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", false);

        audit.List(Owner, plan.Id)
            .Should().Contain(e => e.Kind == SpotifyAuditEventKind.PlanConfirmed && e.ApplicationUser == "Paul");
    }

    // ─── undo ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildsAnUndoThatNeedsItsOwnConfirmation()
    {
        var (executor, _, store) = Build();
        var plan = Save(store, ReplacePlan());
        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", true);

        var undo = await executor.BuildUndoAsync(Owner, plan.Id);

        undo.Status.Should().Be(SpotifyPlanStatus.Draft);
        undo.UndoOfPlanId.Should().Be(plan.Id);
        undo.Preview!.RequiresHighImpactAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public async Task TheUndoRestoresTheOrderCapturedBeforeTheChange()
    {
        var (executor, _, store) = Build();
        var plan = Save(store, ReplacePlan());
        await executor.ConfirmAndExecuteAsync(Owner, plan.Id, "Paul", true);

        var undo = await executor.BuildUndoAsync(Owner, plan.Id);

        var restore = undo.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.ReplaceItems);
        restore.Uris.Should().Equal("spotify:track:before-1", "spotify:track:before-2");
    }

    [Fact]
    public async Task RefusesToUndoAPlanThatNeverRan()
    {
        var (executor, _, store) = Build();
        var plan = Save(store, AddPlan());

        var act = () => executor.BuildUndoAsync(Owner, plan.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    private static (ISpotifyPlanExecutor, FakeSpotify, InMemoryPlanStore) Build()
    {
        var (executor, spotify, store, _) = BuildWithAudit();
        return (executor, spotify, store);
    }

    private static (ISpotifyPlanExecutor, FakeSpotify, InMemoryPlanStore, InMemoryAudit) BuildWithAudit()
    {
        var spotify = new FakeSpotify();
        var store = new InMemoryPlanStore();
        var audit = new InMemoryAudit();
        var executor = new SpotifyPlanExecutor(
            spotify, new FakeInventory(spotify), store, audit, new StubTokens(), new StubClock(Now));

        return (executor, spotify, store, audit);
    }

    /// <summary>Stores the plan exactly as given — a test that sets a status means it.</summary>
    private static SpotifyChangePlan Save(InMemoryPlanStore store, SpotifyChangePlan plan)
    {
        store.Save(Owner, plan);
        return plan;
    }

    private static SpotifyChangePlan AddPlan() =>
        SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.AddItems, [new SpotifyPlanTarget("p1", "Mine", "snap-before")], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps = [new SpotifyPlanStep(0, SpotifyPlanStepKind.AddItems, "p1", "Mine",
                Uris: ["spotify:track:new"])],
            Preview = new SpotifyPlanPreview("Add 1", "Add", [], [], false)
        };

    private static SpotifyChangePlan ReplacePlan() =>
        SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.ReplaceItems, [new SpotifyPlanTarget("p1", "Mine", "snap-before")], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps = [new SpotifyPlanStep(0, SpotifyPlanStepKind.ReplaceItems, "p1", "Mine",
                Uris: ["spotify:track:new"])],
            Preview = new SpotifyPlanPreview("Replace", "Replace", [], [], RequiresHighImpactAcknowledgement: true)
        };

    private static SpotifyChangePlan CreateThenAddPlan() =>
        SpotifyPlanStateMachine.Create(SpotifyPlanAction.CreatePlaylist, [], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps =
            [
                new SpotifyPlanStep(0, SpotifyPlanStepKind.CreatePlaylist, null, "New", Name: "New"),
                new SpotifyPlanStep(1, SpotifyPlanStepKind.AddItems, null, "New", Uris: ["spotify:track:new"])
            ],
            Preview = new SpotifyPlanPreview("Create", "Create", [], [], false)
        };

    /// <summary>Create, then two adds — the second must never run once the first fails.</summary>
    private static SpotifyChangePlan ThreeStepPlan() =>
        SpotifyPlanStateMachine.Create(SpotifyPlanAction.CreatePlaylist, [], Now) with
        {
            Status = SpotifyPlanStatus.AwaitingConfirmation,
            Steps =
            [
                new SpotifyPlanStep(0, SpotifyPlanStepKind.CreatePlaylist, null, "New", Name: "New"),
                new SpotifyPlanStep(1, SpotifyPlanStepKind.AddItems, null, "New", Uris: ["spotify:track:one"]),
                new SpotifyPlanStep(2, SpotifyPlanStepKind.AddItems, null, "New", Uris: ["spotify:track:two"])
            ],
            Preview = new SpotifyPlanPreview("Create", "Create", [], [], false)
        };

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryPlanStore : ISpotifyPlanStore
    {
        private readonly Dictionary<(string, Guid), SpotifyChangePlan> _plans = [];

        public SpotifyChangePlan? Get(string ownerKey, Guid planId) =>
            _plans.TryGetValue((ownerKey, planId), out var plan) ? plan : null;

        public IReadOnlyList<SpotifyChangePlan> List(string ownerKey, int limit = 50) =>
            _plans.Where(kv => kv.Key.Item1 == ownerKey).Select(kv => kv.Value).ToList();

        public void Save(string ownerKey, SpotifyChangePlan plan) => _plans[(ownerKey, plan.Id)] = plan;
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
                playlist, spotify.ExistingItems, SpotifyContentsAccess.Available, playlist.SnapshotId));

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
        public int AddCalls;
        public int ReplaceCalls;
        public bool FailAddItems;
        public bool PlaylistExists = true;
        public string CurrentSnapshot = "snap-before";
        public HttpStatusCode FailureStatus = HttpStatusCode.InternalServerError;
        public string? LastAddPlaylistId;

        public IReadOnlyList<SpotifyPlaylistItemDto> ExistingItems { get; } =
        [
            new(0, SpotifyItemKind.Track, "b1", "Before 1", "spotify:track:before-1", "A", "Al", 1, null, false, null),
            new(1, SpotifyItemKind.Track, "b2", "Before 2", "spotify:track:before-2", "A", "Al", 1, null, false, null)
        ];

        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) =>
            Task.FromResult(PlaylistExists
                ? new SpotifyPlaylistDto(playlistId, "Mine", null, 2, null, IsOwnedByUser: true,
                    IsPublic: false, SnapshotId: CurrentSnapshot)
                : null);

        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(
            string name, string? description = null, bool isPublic = false, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistDto("new-playlist", name, null, 0, null,
                IsOwnedByUser: true, SnapshotId: "snap-new"));

        /// <summary>1-based index of the add call that should fail, if any.</summary>
        public int? FailOnAddCall;

        public Task<string?> AddItemsAsync(
            string playlistId, IReadOnlyList<string> uris, CancellationToken token = default)
        {
            AddCalls++;
            LastAddPlaylistId = playlistId;

            if (FailAddItems || FailOnAddCall == AddCalls)
                throw new SpotifyApiException(FailureStatus, "denied", null, null);

            return Task.FromResult<string?>("snap-after");
        }

        public Task<string?> ReplaceItemsAsync(
            string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default)
        {
            ReplaceCalls++;
            return Task.FromResult<string?>("snap-after");
        }

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
        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsForOwnerAsync(
            string ownerKey, CancellationToken token = default) => GetUserPlaylistsAsync(token);
        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistItemsPageDto(playlistId, ExistingItems, 2, 0, 50, false));
        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsForOwnerAsync(
            string ownerKey, string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            GetPlaylistItemsAsync(playlistId, offset, limit, token);
        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>([]);
        public Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksAsync(
            int limit = 50, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentTrackDto>>([]);
        public Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksForOwnerAsync(
            string ownerKey, int limit = 50, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentTrackDto>>([]);
        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) => Task.FromResult(new SpotifyTopItemsDto(kind, timeRange, []));
        public Task<SpotifyTopItemsDto> GetTopItemsForOwnerAsync(
            string ownerKey, string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) => GetTopItemsAsync(kind, timeRange, limit, token);
        public Task<SpotifySearchResultDto> SearchTracksAsync(
            string query, int limit = 10, CancellationToken token = default) =>
            Task.FromResult(new SpotifySearchResultDto([], 0));
        public Task AddTracksToPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) => Task.CompletedTask;
        public Task RemoveTracksFromPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) => Task.CompletedTask;
        public Task RemovePlaylistsFromLibraryAsync(
            List<string> playlistUris, CancellationToken token = default) => Task.CompletedTask;
    }
}
