using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The builder decides what a change *means* before anything is written. Most of
/// these tests are about refusal: the cases where building nothing is the correct
/// output, because a plan that cannot be reviewed honestly should not exist.
/// </summary>
public class SpotifyPlanBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    // ─── create from a draft ─────────────────────────────────────────────────

    [Fact]
    public void BuildsACreateThenAddPlanFromADraft()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 3), null, false, Now);

        result.Refused.Should().BeFalse();
        var steps = result.Plan!.OrderedSteps;
        steps.Should().HaveCount(2);
        steps[0].Kind.Should().Be(SpotifyPlanStepKind.CreatePlaylist);
        steps[1].Kind.Should().Be(SpotifyPlanStepKind.AddItems);
        steps[1].Uris.Should().HaveCount(3);
    }

    [Fact]
    public void LeavesTheAddStepsPlaylistIdUnsetBecauseItDoesNotExistYet()
    {
        // The executor fills it in from the created playlist. A plan cannot name an
        // ID Spotify has not issued.
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 2), null, false, Now);

        result.Plan!.OrderedSteps[1].PlaylistId.Should().BeNull();
    }

    [Fact]
    public void CountsUnresolvedCandidatesAsAWarningRatherThanSubstitutingThem()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 2, unresolved: 3), null, false, Now);

        result.Plan!.Preview!.ItemsUnresolved.Should().Be(3);
        result.Plan.Preview.Warnings.Should().ContainSingle().Which.Should().Contain("left out");
    }

    [Fact]
    public void RefusesToCreateAPlaylistWhenNothingResolved()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 0, unresolved: 5), null, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("nothing to add");
    }

    [Fact]
    public void UsesTheNameOverrideWhenGiven()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 1), "Southern Crossroads", false, Now);

        result.Plan!.OrderedSteps[0].Name.Should().Be("Southern Crossroads");
    }

    [Fact]
    public void CreatesPrivateByDefaultAndSaysSo()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 1), null, isPublic: false, Now);

        result.Plan!.OrderedSteps[0].IsPublic.Should().BeFalse();
        result.Plan.Preview!.Effects[0].Should().Contain("private");
    }

    [Fact]
    public void MarksACreateAsAdditiveNeedingOnlyOneConfirmation()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 1), null, false, Now);

        result.Plan!.SafetyTier.Should().Be(SpotifyPlanSafetyTier.Additive);
        result.Plan.Preview!.RequiresHighImpactAcknowledgement.Should().BeFalse();
    }

    // ─── add items ───────────────────────────────────────────────────────────

    [Fact]
    public void SkipsTracksAlreadyInThePlaylistRatherThanDuplicatingThem()
    {
        var target = Contents("p", "Mine", Track("a"), Track("b"));

        var result = SpotifyPlanBuilder.AddItems(target, [Uri("b"), Uri("c")], Now);

        result.Plan!.OrderedSteps[0].Uris.Should().ContainSingle().Which.Should().Be(Uri("c"));
        result.Plan.Preview!.ItemsSkippedAsDuplicates.Should().Be(1);
    }

    [Fact]
    public void RefusesWhenEverythingRequestedIsAlreadyThere()
    {
        var target = Contents("p", "Mine", Track("a"));

        var result = SpotifyPlanBuilder.AddItems(target, [Uri("a")], Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("already in");
    }

    [Fact]
    public void DeduplicatesTheRequestItself()
    {
        var result = SpotifyPlanBuilder.AddItems(Contents("p", "Mine"), [Uri("a"), Uri("a")], Now);

        result.Plan!.OrderedSteps[0].Uris.Should().ContainSingle();
    }

    // ─── refusals that protect the user ──────────────────────────────────────

    [Theory]
    [InlineData(SpotifyContentsAccess.Forbidden)]
    [InlineData(SpotifyContentsAccess.Unavailable)]
    public void RefusesToEditAPlaylistItCannotRead(SpotifyContentsAccess access)
    {
        // Without contents there is no honest diff and no restore manifest, so an
        // edit here would be both unreviewable and un-undoable.
        var target = Contents("p", "Hidden", access: access);

        SpotifyPlanBuilder.AddItems(target, [Uri("a")], Now).Refusal.Should().Contain("will not let me read");
        SpotifyPlanBuilder.RemoveItems(target, [Uri("a")], Now).Refusal.Should().Contain("will not let me read");
        SpotifyPlanBuilder.ReplaceItems(target, [Uri("a")], Now).Refusal.Should().Contain("will not let me read");
    }

    [Fact]
    public void RefusesToEditAPlaylistTheUserOnlyFollows()
    {
        var followed = Contents("p", "Theirs", owned: false, items: [Track("a")]);

        SpotifyPlanBuilder.AddItems(followed, [Uri("b")], Now).Refusal.Should().Contain("not yours");
    }

    [Fact]
    public void AllowsEditingACollaborativePlaylistYouDoNotOwn()
    {
        var collaborative = Contents("p", "Ours", owned: false, collaborative: true, items: [Track("a")]);

        SpotifyPlanBuilder.AddItems(collaborative, [Uri("b")], Now).Refused.Should().BeFalse();
    }

    [Fact]
    public void RefusesAPlanBiggerThanTheItemCeiling()
    {
        var many = Enumerable.Range(0, SpotifyPlanBuilder.MaxItemMutationsPerPlan + 1)
            .Select(i => Uri($"t{i}")).ToList();

        var result = SpotifyPlanBuilder.AddItems(Contents("p", "Mine"), many, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("split it");
    }

    [Fact]
    public void AllowsExactlyTheCeiling()
    {
        var atLimit = Enumerable.Range(0, SpotifyPlanBuilder.MaxItemMutationsPerPlan)
            .Select(i => Uri($"t{i}")).ToList();

        SpotifyPlanBuilder.AddItems(Contents("p", "Mine"), atLimit, Now).Refused.Should().BeFalse();
    }

    // ─── rename and details ──────────────────────────────────────────────────

    [Fact]
    public void BuildsARenamePlan()
    {
        var result = SpotifyPlanBuilder.Rename(Playlist("p", "Old"), "New", Now);

        result.Plan!.OrderedSteps[0].Name.Should().Be("New");
        result.Plan.SafetyTier.Should().Be(SpotifyPlanSafetyTier.Modifying);
    }

    [Fact]
    public void RefusesARenameThatChangesNothing()
    {
        SpotifyPlanBuilder.Rename(Playlist("p", "Same"), "Same", Now)
            .Refusal.Should().Contain("already called that");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesABlankNewName(string newName)
    {
        SpotifyPlanBuilder.Rename(Playlist("p", "Old"), newName, Now).Refused.Should().BeTrue();
    }

    [Fact]
    public void WarnsWhenMakingAPlaylistPublic()
    {
        var result = SpotifyPlanBuilder.ChangeDetails(Playlist("p", "Mine"), null, null, true, Now);

        result.Plan!.Preview!.Warnings.Should().ContainSingle().Which.Should().Contain("anyone with the link");
    }

    [Fact]
    public void RefusesADetailsChangeThatWouldChangeNothing()
    {
        SpotifyPlanBuilder.ChangeDetails(Playlist("p", "Mine"), "Mine", null, null, Now)
            .Refusal.Should().Contain("Nothing about");
    }

    // ─── remove ──────────────────────────────────────────────────────────────

    [Fact]
    public void RecordsEveryPositionARemovalWouldAffect()
    {
        var target = Contents("p", "Mine", Track("a"), Track("b"), Track("a"));

        var result = SpotifyPlanBuilder.RemoveItems(target, [Uri("a")], Now);

        result.Plan!.OrderedSteps[0].Positions.Should().Equal(0, 2);
        result.Plan.Preview!.ItemsRemoved.Should().Be(2);
    }

    [Fact]
    public void WarnsThatRemovingALocalFileCannotBeUndone()
    {
        var target = new SpotifyPlaylistContents(
            Playlist("p", "Mine"),
            [new SpotifyPlaylistItemDto(0, SpotifyItemKind.Local, null, "Home Recording",
                "spotify:local:x", "Me", null, 1000, null, true, null)],
            SpotifyContentsAccess.Available, "snap");

        var result = SpotifyPlanBuilder.RemoveItems(target, ["spotify:local:x"], Now);

        result.Plan!.Preview!.Warnings.Should().ContainSingle().Which.Should().Contain("cannot be undone");
    }

    [Fact]
    public void RefusesToRemoveSomethingThatIsNotThere()
    {
        SpotifyPlanBuilder.RemoveItems(Contents("p", "Mine", Track("a")), [Uri("zzz")], Now)
            .Refusal.Should().Contain("could not find");
    }

    // ─── replace: the high-impact one ────────────────────────────────────────

    [Fact]
    public void RequiresASecondAcknowledgementToReplaceContents()
    {
        var result = SpotifyPlanBuilder.ReplaceItems(Contents("p", "Mine", Track("a")), [Uri("b")], Now);

        result.Plan!.SafetyTier.Should().Be(SpotifyPlanSafetyTier.HighImpact);
        result.Plan.Preview!.RequiresHighImpactAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public void SaysHowMuchAReplaceWouldDiscard()
    {
        var target = Contents("p", "Mine", Track("a"), Track("b"), Track("c"));

        var result = SpotifyPlanBuilder.ReplaceItems(target, [Uri("a")], Now);

        result.Plan!.Preview!.ItemsRemoved.Should().Be(2);
        result.Plan.Preview.Warnings[0].Should().Contain("discards the current contents");
    }

    // ─── reorder ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildsAReorderPlan()
    {
        var result = SpotifyPlanBuilder.ReorderItems(
            Contents("p", "Mine", Track("a"), Track("b"), Track("c")), 2, 0, 1, Now);

        result.Plan!.OrderedSteps[0].RangeStart.Should().Be(2);
        result.Plan.OrderedSteps[0].InsertBefore.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    public void RefusesAReorderOutsideThePlaylist(int rangeStart, int insertBefore)
    {
        SpotifyPlanBuilder.ReorderItems(
            Contents("p", "Mine", Track("a"), Track("b")), rangeStart, insertBefore, 1, Now)
            .Refused.Should().BeTrue();
    }

    [Fact]
    public void RefusesAReorderThatWouldChangeNothing()
    {
        SpotifyPlanBuilder.ReorderItems(Contents("p", "Mine", Track("a"), Track("b")), 1, 1, 1, Now)
            .Refusal.Should().Contain("exactly as it is");
    }

    // ─── plan lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public void EveryBuiltPlanStartsAsADraftWithAnExpiry()
    {
        var result = SpotifyPlanBuilder.CreateFromDraft(Draft(resolved: 1), null, false, Now);

        result.Plan!.Status.Should().Be(SpotifyPlanStatus.Draft);
        result.Plan.ExpiresAtUtc.Should().BeAfter(Now);
    }

    [Fact]
    public void RecordsTheSnapshotItPlannedAgainstSoExecutionCanDetectDrift()
    {
        var result = SpotifyPlanBuilder.AddItems(Contents("p", "Mine"), [Uri("a")], Now);

        result.Plan!.Targets.Should().ContainSingle().Which.SnapshotId.Should().Be("snap");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string Uri(string id) => $"spotify:track:{id}";

    private static SpotifyPlaylistDto Playlist(string id, string name, bool owned = true, bool collab = false) =>
        new(id, name, null, 0, null, IsOwnedByUser: owned, IsCollaborative: collab,
            IsPublic: false, SnapshotId: "snap");

    private static SpotifyPlaylistItemDto Track(string id) =>
        new(0, SpotifyItemKind.Track, id, id, Uri(id), "Artist", "Album", 1000, null, false, null);

    private static SpotifyPlaylistContents Contents(
        string id, string name,
        params SpotifyPlaylistItemDto[] items) =>
        Contents(id, name, true, false, SpotifyContentsAccess.Available, items);

    private static SpotifyPlaylistContents Contents(
        string id, string name, bool owned = true, bool collaborative = false,
        SpotifyContentsAccess access = SpotifyContentsAccess.Available,
        SpotifyPlaylistItemDto[]? items = null) =>
        new(Playlist(id, name, owned, collaborative),
            (items ?? []).Select((item, index) => item with { Position = index }).ToList(),
            access, "snap");

    private static SpotifyDiscoveryDraft Draft(int resolved, int unresolved = 0)
    {
        var candidates = new List<SpotifyDiscoveryCandidate>();

        for (var i = 0; i < resolved; i++)
        {
            candidates.Add(new SpotifyDiscoveryCandidate(
                $"c{i}", i, "Artist", $"Title {i}", null, SpotifyCandidateResolution.Resolved,
                new SpotifyTrackDto($"t{i}", $"Title {i}", Uri($"t{i}"), 1000, "Artist", "Album", null, null),
                [], false, "known"));
        }

        for (var i = 0; i < unresolved; i++)
        {
            candidates.Add(new SpotifyDiscoveryCandidate(
                $"u{i}", resolved + i, "Artist", $"Missing {i}", null,
                SpotifyCandidateResolution.NotFound, null, [], true, "unknown"));
        }

        return new SpotifyDiscoveryDraft(
            "draft-1", SpotifyDiscoveryDraftState.Ready, "Delta Blues Starter", "A summary",
            ["make me a blues playlist"], 25, null, candidates, "coverage", Now, Now);
    }
}
