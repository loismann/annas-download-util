using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Where the deletion boundary sits now that phase 8 has landed.
///
/// Spotify has no delete-playlist operation and never has: the strongest thing that
/// exists is removing a playlist from your own library, which is an unfollow. Other
/// followers keep it, and the playlist itself is untouched.
///
/// Phase 8 wires that removal up deliberately. These tests pin the four things that
/// make it safe rather than pretending the capability is absent — it isn't any more,
/// and a test that claimed otherwise would be lying:
///
///   1. Nothing is ever described, to the model or the user, as deleting.
///   2. Removal only ever happens by executing a confirmed, acknowledged plan.
///   3. The assistant never chooses which playlists are on the list.
///   4. A playlist Spotify would not let us read can never be on the list at all.
/// </summary>
public class SpotifyNoPlaylistDeletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── 1. nothing is called deletion ───────────────────────────────────────

    [Theory]
    [InlineData("delete_playlist")]
    [InlineData("remove_playlist")]
    [InlineData("delete_playlists")]
    [InlineData("unfollow_playlist")]
    [InlineData("purge_library")]
    public void NoDeletionVerbIsADispatchableAction(string wireName)
    {
        SpotifyActionCatalog.Parse(wireName).Should().Be(SpotifyReadAction.Unknown);
    }

    [Fact]
    public void NoActionTheModelCanEmitIsNamedForDeletion()
    {
        // The wire names are what the model chooses between. None of them offers
        // deletion, so there is no name to pick that would mean it.
        SpotifyActionCatalog.All
            .Select(definition => definition.WireName)
            .Should().OnlyContain(name => !name.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheModelIsToldRemovalIsAnUnfollowRatherThanADeletion()
    {
        // The prompt is generated from the catalog, so this is what the model reads.
        SpotifyActionCatalog.PromptActionList()
            .Should().Contain("unfollow; there is no delete");
    }

    [Fact]
    public void WhatTheRemovalPlanSaysItWillDoIsRemove()
    {
        var result = SpotifyPlanBuilder.RemoveFromLibrary([Readable("p1", "Old Mix")], Now);
        var preview = result.Plan!.Preview!;

        // Effects are the claims about what happens. None of them may claim deletion;
        // the warnings are where the word appears, and only to deny it.
        preview.Effects.Should().OnlyContain(
            effect => !effect.Contains("delet", StringComparison.OrdinalIgnoreCase));
        preview.Summary.Should().NotContain("delet");

        preview.Warnings.Should().Contain(w => w.Contains("unfollow, not a delete"));
        preview.Warnings.Should().Contain(w => w.Contains("anyone else who follows these keeps them"));
    }

    // ─── 2. removal only happens through a confirmed plan ────────────────────

    [Fact]
    public void RemovalIsHighImpactAndNeedsTheSecondAcknowledgement()
    {
        var result = SpotifyPlanBuilder.RemoveFromLibrary([Readable("p1", "Old Mix")], Now);

        result.Plan!.SafetyTier.Should().Be(SpotifyPlanSafetyTier.HighImpact);
        result.Plan.Preview!.RequiresHighImpactAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public void ABuiltRemovalPlanHasNotRemovedAnything()
    {
        // The builder is pure. Building is not doing, and the plan comes back needing
        // a confirmation it does not have.
        var result = SpotifyPlanBuilder.RemoveFromLibrary([Readable("p1", "Old Mix")], Now);

        result.Plan!.Status.Should().Be(SpotifyPlanStatus.Draft);
        result.Plan.ConfirmedBy.Should().BeNull();
    }

    [Fact]
    public void OnlyTheExecutorCallsTheLibraryRemovalMethod()
    {
        // If a second caller ever appears, removal has escaped the plan flow — which
        // is the whole safety model, not a detail.
        var callers = Directory
            .EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/") && !path.Contains("/bin/"))
            .Where(path => File.ReadAllText(path).Contains("RemovePlaylistsFromLibraryAsync("))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        callers.Should().Equal("SpotifyPlanExecutor.cs", "SpotifyService.cs");
    }

    // ─── 3. the assistant never picks the targets ────────────────────────────

    [Fact]
    public async Task RefusesToRemoveAnythingWhenTheUserNamedNoPlaylists()
    {
        var (service, _) = BuildConversation(new SpotifyCommandArguments(Query: "just tidy things up"));

        var response = await service.HandleAsync(new SpotifyConversationRequest("clean up my spotify"));

        response.Message.Should().Contain("I will not pick them for you");
    }

    [Fact]
    public async Task NeverBuildsAPlanWhenItWasNotToldWhichPlaylists()
    {
        // The message alone could be cosmetic. This asserts no plan reached the store.
        var (service, plans) = BuildConversation(new SpotifyCommandArguments(Query: "sort it out for me"));

        await service.HandleAsync(new SpotifyConversationRequest("sort out my library"));

        plans.Built.Should().BeEmpty();
    }

    [Fact]
    public async Task MergingWithoutNamedPlaylistsIsNotEvenDispatchable()
    {
        // Validation rejects it before any handler runs, so there is no path where a
        // merge resolves its own sources.
        var command = SpotifyActionCatalog.Validate(new SpotifyCommandEnvelope(
            SpotifyActionCatalog.SchemaVersion, "plan_merge_playlists",
            new SpotifyCommandArguments(Query: "merge the messy ones"), Confidence: 0.99));

        command.Action.Should().Be(SpotifyReadAction.Unknown);
        command.Clarification.Should().Contain("Which playlists");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ClearingOutTheEmptyOnesOnlyEverProposesProvablyEmptyPlaylists()
    {
        // The single place the assistant picks targets rather than being told them.
        // It may only propose playlists Spotify let it read *and* that held nothing —
        // an unreadable playlist also reports zero items and must never come along.
        var library = new LibrarySpotify(
            (Id: "empty-1", Name: "Nothing Here", Readable: true, Items: 0),
            (Id: "full-1", Name: "Full Of Songs", Readable: true, Items: 3),
            (Id: "hidden-1", Name: "Followed Mix", Readable: false, Items: 0));

        var plans = new RecordingPlanService();
        var service = new SpotifyConversationService(
            new StubParser(SpotifyReadAction.PlanRemovePlaylistsFromLibrary,
                new SpotifyCommandArguments(Query: "get rid of the empty playlists")),
            library, new SpotifyInventoryService(library), new StubInventoryJobs(),
            new StubCurrentUser(), new StubKnownMusic(), new StubDiscovery(), plans);

        await service.HandleAsync(new SpotifyConversationRequest("clear out the empty ones"));

        plans.Built.Should().ContainSingle()
            .Which.PlaylistIds.Should().Equal("empty-1");
    }

    // ─── 4. unreadable is unknown, never a removal candidate ─────────────────

    [Fact]
    public void RefusesToRemoveAPlaylistItCouldNotReadInside()
    {
        var unreadable = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p9", "Followed Mix", null, null, null, IsOwnedByUser: false),
            [], SpotifyContentsAccess.Forbidden, null);

        var result = SpotifyPlanBuilder.RemoveFromLibrary([unreadable], Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("Unreadable is unknown, not empty");
    }

    [Fact]
    public void OneUnreadablePlaylistRefusesTheWholeBatch()
    {
        // Not "removes the readable ones anyway". A batch the user reviewed as five
        // playlists must not quietly become four.
        var unreadable = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p9", "Followed Mix", null, null, null, IsOwnedByUser: false),
            [], SpotifyContentsAccess.Forbidden, null);

        var result = SpotifyPlanBuilder.RemoveFromLibrary(
            [Readable("p1", "Old Mix"), unreadable, Readable("p2", "Older Mix")], Now);

        result.Refused.Should().BeTrue();
        result.Plan.Should().BeNull();
    }

    [Fact]
    public void AnEmptyPlaylistIsOnlyEmptyWhenSpotifyLetUsLook()
    {
        // The analysis is what feeds "clear out the empty ones". An unreadable
        // playlist reports zero items, and this is what stops that reading as empty.
        var unreadable = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p9", "Followed Mix", null, null, null, IsOwnedByUser: false),
            [], SpotifyContentsAccess.Forbidden, null);

        var empty = SpotifyAnalysis.FindEmpty([unreadable]);

        empty.Should().BeEmpty();
    }

    // ─── the merge gate ──────────────────────────────────────────────────────

    [Fact]
    public void AMergeVerifiesTheTargetBeforeAnySourceIsRemoved()
    {
        var result = SpotifyPlanBuilder.Merge(
            [Readable("p1", "Road Trip", "spotify:track:a"), Readable("p2", "Road Trip 2", "spotify:track:b")],
            existingTarget: null, newTargetName: "Road Trips", isPublic: false, removeSources: true, Now);

        var steps = result.Plan!.OrderedSteps;
        var verify = steps.Single(s => s.Kind == SpotifyPlanStepKind.VerifyPlaylistPopulated);
        var firstRemoval = steps.First(s => s.Kind == SpotifyPlanStepKind.RemoveFromLibrary);

        verify.Ordinal.Should().BeLessThan(firstRemoval.Ordinal);
    }

    [Fact]
    public void AMergeLeavesTheOriginalsAloneUnlessRemovalWasAskedFor()
    {
        var result = SpotifyPlanBuilder.Merge(
            [Readable("p1", "Road Trip", "spotify:track:a"), Readable("p2", "Road Trip 2", "spotify:track:b")],
            existingTarget: null, newTargetName: "Road Trips", isPublic: false, removeSources: false, Now);

        result.Plan!.OrderedSteps.Should().NotContain(s => s.Kind == SpotifyPlanStepKind.RemoveFromLibrary);
        result.Plan.Preview!.Effects.Should().Contain(e => e.Contains("Leave all 2 original playlists"));
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    private static SpotifyPlaylistContents Readable(string id, string name, params string[] uris) =>
        new(new SpotifyPlaylistDto(id, name, null, uris.Length, null, IsOwnedByUser: true, SnapshotId: "snap-" + id),
            uris.Select((uri, index) => new SpotifyPlaylistItemDto(
                index, SpotifyItemKind.Track, "t" + index, "Track " + index, uri,
                "Artist " + index, "Album", 1000, null, false, null)).ToList(),
            SpotifyContentsAccess.Available, "snap-" + id);

    private static (ISpotifyConversationService, RecordingPlanService) BuildConversation(
        SpotifyCommandArguments arguments)
    {
        var plans = new RecordingPlanService();
        var spotify = new SilentSpotify();
        var service = new SpotifyConversationService(
            new StubParser(SpotifyReadAction.PlanRemovePlaylistsFromLibrary, arguments),
            spotify,
            new SpotifyInventoryService(spotify),
            new StubInventoryJobs(),
            new StubCurrentUser(),
            new StubKnownMusic(),
            new StubDiscovery(),
            plans);

        return (service, plans);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        return Path.Combine(directory!.FullName, "src");
    }

    private sealed class RecordingPlanService : ISpotifyPlanService
    {
        public List<SpotifyBuildPlanRequest> Built { get; } = [];

        public Task<SpotifyPlanBuilder.Result> BuildAsync(
            string ownerKey, SpotifyBuildPlanRequest request, CancellationToken token = default)
        {
            Built.Add(request);
            return Task.FromResult(SpotifyPlanBuilder.Result.Refuse("not reached"));
        }

        public SpotifyPlanDto? Get(string ownerKey, Guid planId) => null;
        public IReadOnlyList<SpotifyPlanDto> List(string ownerKey, int limit = 25) => [];
        public SpotifyPlanDto? Cancel(string ownerKey, Guid planId) => null;
    }

    private sealed class StubParser(SpotifyReadAction action, SpotifyCommandArguments arguments)
        : ISpotifyCommandParser
    {
        public Task<SpotifyValidatedCommand> ParseAsync(
            string message, string? conversationContext = null, CancellationToken token = default) =>
            Task.FromResult(new SpotifyValidatedCommand(action, arguments, 1.0));
    }

    private sealed class StubCurrentUser : ISpotifyCurrentUser
    {
        public string GetRequiredOwnerKey() => "owner";
        public string? TryGetOwnerKey() => "owner";
    }

    private sealed class StubInventoryJobs : ISpotifyInventoryJobService
    {
        public SpotifyInventoryStatusDto Start(string ownerKey) => GetStatus(ownerKey);

        public SpotifyInventoryStatusDto GetStatus(string ownerKey) => new(
            null, SpotifyInventoryJobState.Complete, 0, 0, 0, 0, 0,
            null, null, null, DateTimeOffset.UtcNow);

        public Task CancelAsync(string ownerKey) => Task.CompletedTask;
    }

    private sealed class StubKnownMusic : ISpotifyKnownMusicService
    {
        public Task<SpotifyKnownMusicReport> GetAsync(CancellationToken token = default) =>
            Task.FromResult(new SpotifyKnownMusicReport(
                new SpotifyKnownMusicIndex(new HashSet<string>(), new HashSet<string>(), 0, 0, false, false),
                "", DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentContextsAsync(
            CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>([]);

        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind, string timeRange, int limit, CancellationToken token = default) =>
            Task.FromResult(new SpotifyTopItemsDto(kind, timeRange, []));

        public SpotifyKnownMusicOverrideResult ApplyOverride(SpotifyKnownMusicOverrideRequest request) =>
            new(request.Kind, request.Name, request.Known, DateTimeOffset.UtcNow);
    }

    private sealed class StubDiscovery : ISpotifyDiscoveryService
    {
        public Task<SpotifyDiscoveryDraft> CreateAsync(
            string message, int size = 25, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<SpotifyDiscoveryDraft> RefineAsync(
            string draftId, string message, int? size = null, CancellationToken token = default) =>
            throw new NotSupportedException();

        public SpotifyDiscoveryDraft? Get(string draftId) => null;
        public bool Delete(string draftId) => false;
        public IReadOnlyList<SpotifyDiscoveryDraft> ListSaved() => [];
        public SpotifyDiscoveryDraft Update(string draftId, SpotifyDiscoveryDraftUpdateRequest request) =>
            throw new NotSupportedException();
    }

    /// <summary>A small library where some playlists cannot be read inside.</summary>
    private sealed class LibrarySpotify(
        params (string Id, string Name, bool Readable, int Items)[] playlists) : SilentSpotify
    {
        public override Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) =>
            Task.FromResult(playlists
                .Select(p => new SpotifyPlaylistDto(
                    p.Id, p.Name, null, p.Readable ? p.Items : null, null,
                    IsOwnedByUser: p.Readable, SnapshotId: "snap-" + p.Id))
                .ToList());

        public override Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default)
        {
            var playlist = playlists.Single(p => p.Id == playlistId);

            var items = Enumerable.Range(0, playlist.Items)
                .Select(i => new SpotifyPlaylistItemDto(
                    i, SpotifyItemKind.Track, "t" + i, "Track " + i, $"spotify:track:{playlistId}-{i}",
                    "Artist", "Album", 1000, null, false, null))
                .ToList();

            return Task.FromResult(new SpotifyPlaylistItemsPageDto(
                playlistId, items, playlist.Items, offset, limit, false,
                playlist.Readable ? SpotifyContentsAccess.Available : SpotifyContentsAccess.Forbidden,
                "snap-" + playlistId));
        }
    }

    /// <summary>Fails loudly if the refusal path reaches Spotify at all.</summary>
    private class SilentSpotify : ISpotifyService
    {
        public Task<SpotifySearchResultDto> SearchTracksAsync(
            string query, int limit = 10, CancellationToken token = default) =>
            Task.FromResult(new SpotifySearchResultDto([], 0));

        public virtual Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) =>
            Task.FromResult(new List<SpotifyPlaylistDto>());

        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(
            string playlistId, CancellationToken token = default) =>
            Task.FromResult<SpotifyPlaylistDto?>(null);

        public virtual Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistItemsPageDto(playlistId, [], 0, 0, 50, false));

        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
            CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>([]);

        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) =>
            Task.FromResult(new SpotifyTopItemsDto(kind, timeRange, []));

        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(
            string name, string? description = null, bool isPublic = false,
            CancellationToken token = default) => throw new NotSupportedException();

        public Task AddTracksToPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task RemoveTracksFromPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task RemovePlaylistsFromLibraryAsync(
            List<string> playlistUris, CancellationToken token = default) =>
            throw new NotSupportedException("Nothing in a refusal path may remove a playlist.");

        public Task<string?> AddItemsAsync(
            string playlistId, IReadOnlyList<string> uris, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<string?> RemoveItemsAsync(
            string playlistId, IReadOnlyList<string> uris, string? snapshotId = null,
            CancellationToken token = default) => throw new NotSupportedException();

        public Task<string?> ReplaceItemsAsync(
            string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<string?> ReorderItemsAsync(
            string playlistId, int rangeStart, int insertBefore, int rangeLength,
            string? snapshotId = null, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task ChangePlaylistDetailsAsync(
            string playlistId, string? name = null, string? description = null, bool? isPublic = null,
            CancellationToken token = default) => throw new NotSupportedException();
    }
}
