using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The merge and library-removal builders (phase 8).
///
/// Merge is the first plan that touches several playlists at once, which makes two
/// things load-bearing that did not matter before: the order of the steps, because
/// the copy must be proven before the originals are let go of, and the honesty of
/// the count, because the user is approving a number they cannot verify themselves.
/// </summary>
public class SpotifyMergePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── what a merge produces ───────────────────────────────────────────────

    [Fact]
    public void CombinesEveryDistinctTrackFromEverySource()
    {
        var result = Merge(
            [Playlist("p1", "Road Trip", "a", "b"), Playlist("p2", "Road Trip 2", "c")]);

        var add = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.AddItems);
        add.Uris.Should().Equal("spotify:track:a", "spotify:track:b", "spotify:track:c");
    }

    [Fact]
    public void KeepsTheOrderTheSourcesWereGivenIn()
    {
        // "Retain the first encountered ordering" — the only part of the original
        // curation a merge can preserve for free.
        var result = Merge(
            [Playlist("p1", "Second Half", "c", "d"), Playlist("p2", "First Half", "a", "b")]);

        var add = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.AddItems);
        add.Uris.Should().Equal(
            "spotify:track:c", "spotify:track:d", "spotify:track:a", "spotify:track:b");
    }

    [Fact]
    public void AddsASongInTwoSourcesOnlyOnce()
    {
        var result = Merge(
            [Playlist("p1", "Road Trip", "a", "b"), Playlist("p2", "Road Trip 2", "b", "c")]);

        var add = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.AddItems);
        add.Uris.Should().Equal("spotify:track:a", "spotify:track:b", "spotify:track:c");
        add.Uris!.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SaysHowManyRepeatsItCollapsed()
    {
        // Deduplication is invisible in the result, so it has to be stated. Otherwise
        // "60 tracks became 55" looks like something went missing.
        var result = Merge(
            [Playlist("p1", "Road Trip", "a", "b"), Playlist("p2", "Road Trip 2", "b", "c")]);

        result.Plan!.Preview!.Warnings.Should().Contain(w => w.Contains("1 exact repeat"));
        result.Plan.Preview.ItemsSkippedAsDuplicates.Should().Be(1);
    }

    [Fact]
    public void ListsEverySourcePlaylistByName()
    {
        // "All" and "everything" require a resolved target list displayed before
        // confirmation. The preview is that list.
        var result = Merge(
            [Playlist("p1", "Road Trip", "a"), Playlist("p2", "Road Trip 2", "b")]);

        var effects = result.Plan!.Preview!.Effects;
        effects.Should().Contain(e => e.Contains("“Road Trip”"));
        effects.Should().Contain(e => e.Contains("“Road Trip 2”"));
    }

    [Fact]
    public void CreatesANewPrivatePlaylistWhenNoTargetWasChosen()
    {
        var result = Merge([Playlist("p1", "A", "a"), Playlist("p2", "B", "b")]);

        var create = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.CreatePlaylist);
        create.Name.Should().Be("Road Trips");
        create.IsPublic.Should().BeFalse();
    }

    [Fact]
    public void AddsToAnExistingTargetInsteadOfCreatingASecondOne()
    {
        var target = Playlist("t1", "Everything", "a");

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", "a", "b"), Playlist("p2", "B", "c")],
            target, newTargetName: null, isPublic: false, removeSources: false, Now);

        result.Plan!.OrderedSteps.Should().NotContain(s => s.Kind == SpotifyPlanStepKind.CreatePlaylist);

        // The track the target already had is not added a second time.
        var add = result.Plan.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.AddItems);
        add.Uris.Should().Equal("spotify:track:b", "spotify:track:c");
        add.PlaylistId.Should().Be("t1");
    }

    // ─── the verification gate ───────────────────────────────────────────────

    [Fact]
    public void VerifiesTheTargetBeforeRemovingAnySource()
    {
        var result = Merge(
            [Playlist("p1", "A", "a"), Playlist("p2", "B", "b")], removeSources: true);

        var kinds = result.Plan!.OrderedSteps.Select(s => s.Kind).ToList();
        kinds.Should().Equal(
            SpotifyPlanStepKind.CreatePlaylist,
            SpotifyPlanStepKind.AddItems,
            SpotifyPlanStepKind.VerifyPlaylistPopulated,
            SpotifyPlanStepKind.RemoveFromLibrary,
            SpotifyPlanStepKind.RemoveFromLibrary);
    }

    [Fact]
    public void TheVerificationExpectsEveryTrackTheMergeClaimed()
    {
        var result = Merge(
            [Playlist("p1", "A", "a", "b"), Playlist("p2", "B", "c")], removeSources: true);

        var verify = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.VerifyPlaylistPopulated);
        verify.ExpectedItemCount.Should().Be(3);
    }

    [Fact]
    public void RemovesEachSourceAsItsOwnStep()
    {
        // One step per playlist: a failure part-way removes strictly fewer, never
        // more, and the audit names exactly which came off.
        var result = Merge(
            [Playlist("p1", "A", "a"), Playlist("p2", "B", "b")], removeSources: true);

        var removals = result.Plan!.OrderedSteps
            .Where(s => s.Kind == SpotifyPlanStepKind.RemoveFromLibrary)
            .ToList();

        removals.Should().HaveCount(2);
        removals.Select(s => s.PlaylistId).Should().Equal("p1", "p2");
        removals.Should().OnlyContain(s => s.Uris!.Single().StartsWith("spotify:playlist:"));
    }

    // ─── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public void RefusesToMergeOnePlaylist()
    {
        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", "a")], null, "Merged", false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("at least two");
    }

    [Fact]
    public void RefusesWhenAnySourceCouldNotBeReadThroughly()
    {
        // A partial view is the one thing a merge must not be built on: the missing
        // items would be silently left behind, and if the sources were then removed
        // there would be no copy of them anywhere.
        var unreadable = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p2", "Followed", null, null, null, IsOwnedByUser: false),
            [], SpotifyContentsAccess.Forbidden, null);

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", "a"), unreadable], null, "Merged", false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("“Followed”");
        result.Refusal.Should().Contain("partial view");
    }

    [Fact]
    public void RefusesWhenTheDestinationIsAlsoOneOfTheSources()
    {
        var target = Playlist("p1", "Road Trip", "a");

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "Road Trip", "a"), Playlist("p2", "B", "b")],
            target, null, false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("both the destination and one of the sources");
    }

    [Fact]
    public void RefusesAMergeThatWouldChangeNothing()
    {
        var target = Playlist("t1", "Everything", "a", "b");

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", "a"), Playlist("p2", "B", "b")],
            target, null, false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("already in “Everything”");
    }

    [Fact]
    public void RefusesAMergeOverTheItemCeiling()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"t{i}").ToArray();
        var more = Enumerable.Range(400, 200).Select(i => $"t{i}").ToArray();

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", many), Playlist("p2", "B", more)], null, "Huge", false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("600 item changes");
    }

    [Fact]
    public void RefusesAMergeOverThePlaylistCeiling()
    {
        var sources = Enumerable.Range(0, 21)
            .Select(i => Playlist($"p{i}", $"List {i}", $"t{i}"))
            .ToList();

        var result = SpotifyPlanBuilder.Merge(sources, null, "Huge", false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("21 playlists at once");
    }

    [Fact]
    public void RefusesToMergeIntoAPlaylistYouOnlyFollow()
    {
        var followed = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("t1", "Someone Else's", null, 0, null,
                IsOwnedByUser: false, IsCollaborative: false),
            [], SpotifyContentsAccess.Available, null);

        var result = SpotifyPlanBuilder.Merge(
            [Playlist("p1", "A", "a"), Playlist("p2", "B", "b")], followed, null, false, false, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("not yours to change");
    }

    // ─── honesty about what is lost ──────────────────────────────────────────

    [Fact]
    public void WarnsThatLocalFilesCannotComeAlong()
    {
        var withLocal = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p1", "Ripped CDs", null, 1, null, IsOwnedByUser: true, SnapshotId: "s1"),
            [new SpotifyPlaylistItemDto(0, SpotifyItemKind.Local, null, "Home Recording", null,
                "Us", null, 1000, null, true, null)],
            SpotifyContentsAccess.Available, "s1");

        var result = SpotifyPlanBuilder.Merge(
            [withLocal, Playlist("p2", "B", "b")], null, "Merged", false, false, Now);

        result.Plan!.Preview!.Warnings.Should().Contain(w =>
            w.Contains("Home Recording") && w.Contains("cannot be"));
    }

    [Fact]
    public void WarnsHarderWhenRemovingSourcesWouldStrandLocalFiles()
    {
        // The local file exists only in the source. Removing the source is the one
        // case where a merge genuinely loses something.
        var withLocal = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p1", "Ripped CDs", null, 1, null, IsOwnedByUser: true, SnapshotId: "s1"),
            [new SpotifyPlaylistItemDto(0, SpotifyItemKind.Local, null, "Home Recording", null,
                "Us", null, 1000, null, true, null)],
            SpotifyContentsAccess.Available, "s1");

        var result = SpotifyPlanBuilder.Merge(
            [withLocal, Playlist("p2", "B", "b")], null, "Merged", false, removeSources: true, Now);

        result.Plan!.Preview!.Warnings.Should().Contain(w =>
            w.Contains("losing the only reference"));
    }

    [Fact]
    public void ReportsProbableRepeatsWithoutDroppingEither()
    {
        // Two different Spotify URIs, same artist and title — a remaster, say.
        // Reported for review; both are kept, because that is the listener's call.
        var left = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p1", "A", null, 1, null, IsOwnedByUser: true, SnapshotId: "s1"),
            [Track(0, "spotify:track:studio", "Mystery Train", "Elvis Presley")],
            SpotifyContentsAccess.Available, "s1");

        var right = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p2", "B", null, 1, null, IsOwnedByUser: true, SnapshotId: "s2"),
            [Track(0, "spotify:track:remaster", "Mystery Train", "Elvis Presley")],
            SpotifyContentsAccess.Available, "s2");

        var result = SpotifyPlanBuilder.Merge([left, right], null, "Merged", false, false, Now);

        var add = result.Plan!.OrderedSteps.Single(s => s.Kind == SpotifyPlanStepKind.AddItems);
        add.Uris.Should().Equal("spotify:track:studio", "spotify:track:remaster");
        result.Plan.Preview!.Warnings.Should().Contain(w => w.Contains("another recording"));
    }

    [Fact]
    public void SaysPlainlyWhenTheOriginalsAreBeingKept()
    {
        var result = Merge([Playlist("p1", "A", "a"), Playlist("p2", "B", "b")]);

        result.Plan!.Preview!.Effects.Should().Contain(e => e.Contains("Leave all 2 original playlists"));
        result.Plan.Preview.ConfirmLabel.Should().Be("Merge them");
    }

    [Fact]
    public void SaysPlainlyWhenTheOriginalsAreBeingRemoved()
    {
        var result = Merge([Playlist("p1", "A", "a"), Playlist("p2", "B", "b")], removeSources: true);

        result.Plan!.Preview!.ConfirmLabel.Should().Be("Merge and remove the originals");
        result.Plan.Preview.Warnings.Should().Contain(w => w.Contains("unfollow, not a delete"));
    }

    [Fact]
    public void EveryMergeNeedsTheSecondAcknowledgement()
    {
        var result = Merge([Playlist("p1", "A", "a"), Playlist("p2", "B", "b")]);

        result.Plan!.SafetyTier.Should().Be(SpotifyPlanSafetyTier.HighImpact);
        result.Plan.Preview!.RequiresHighImpactAcknowledgement.Should().BeTrue();
    }

    // ─── library removal ─────────────────────────────────────────────────────

    [Fact]
    public void RemovalNamesEveryPlaylistAndItsSize()
    {
        var result = SpotifyPlanBuilder.RemoveFromLibrary(
            [Playlist("p1", "Old Mix", "a", "b"), Playlist("p2", "Older Mix")], Now);

        result.Plan!.Preview!.Effects.Should().Equal(
            "Remove “Old Mix” (2 items) from your library",
            "Remove “Older Mix” (0 items) from your library");
    }

    [Fact]
    public void RemovalWarnsWhenSomethingOnTheListIsNotEmpty()
    {
        // The dangerous case: a bulk "clean up" that quietly takes a playlist with
        // 200 songs in it along with the empty ones.
        var result = SpotifyPlanBuilder.RemoveFromLibrary(
            [Playlist("p1", "Old Mix", "a", "b"), Playlist("p2", "Older Mix")], Now);

        result.Plan!.Preview!.Warnings.Should().Contain(w =>
            w.Contains("not empty") && w.Contains("“Old Mix”"));
    }

    [Fact]
    public void RemovalSaysWhenYouDoNotOwnSomethingOnTheList()
    {
        var followed = new SpotifyPlaylistContents(
            new SpotifyPlaylistDto("p1", "Someone Else's", null, 0, null, IsOwnedByUser: false),
            [], SpotifyContentsAccess.Available, null);

        var result = SpotifyPlanBuilder.RemoveFromLibrary([followed], Now);

        result.Plan!.Preview!.Warnings.Should().Contain(w => w.Contains("You do not own"));
    }

    [Fact]
    public void RemovalCollapsesTheSamePlaylistListedTwice()
    {
        // Otherwise it would appear twice in the preview and be counted twice.
        var result = SpotifyPlanBuilder.RemoveFromLibrary(
            [Playlist("p1", "Old Mix"), Playlist("p1", "Old Mix")], Now);

        result.Plan!.OrderedSteps.Should().ContainSingle();
        result.Plan.Preview!.PlaylistsAffected.Should().Be(1);
    }

    [Fact]
    public void RemovalRefusesAnEmptySelection()
    {
        var result = SpotifyPlanBuilder.RemoveFromLibrary([], Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("Which playlists");
    }

    [Fact]
    public void RemovalRefusesMoreThanTheCeiling()
    {
        var many = Enumerable.Range(0, 21).Select(i => Playlist($"p{i}", $"List {i}")).ToList();

        var result = SpotifyPlanBuilder.RemoveFromLibrary(many, Now);

        result.Refused.Should().BeTrue();
        result.Refusal.Should().Contain("21 playlists at once");
    }

    // ─── plumbing ────────────────────────────────────────────────────────────

    private static SpotifyPlanBuilder.Result Merge(
        IReadOnlyList<SpotifyPlaylistContents> sources, bool removeSources = false) =>
        SpotifyPlanBuilder.Merge(sources, null, "Road Trips", false, removeSources, Now);

    private static SpotifyPlaylistContents Playlist(string id, string name, params string[] trackIds) =>
        new(new SpotifyPlaylistDto(id, name, null, trackIds.Length, null,
                IsOwnedByUser: true, SnapshotId: "snap-" + id),
            trackIds.Select((trackId, index) =>
                Track(index, $"spotify:track:{trackId}", $"Track {trackId}", $"Artist {trackId}")).ToList(),
            SpotifyContentsAccess.Available, "snap-" + id);

    private static SpotifyPlaylistItemDto Track(int position, string uri, string name, string artist) =>
        new(position, SpotifyItemKind.Track, uri[(uri.LastIndexOf(':') + 1)..], name, uri,
            artist, "Album", 1000, null, false, null);
}
