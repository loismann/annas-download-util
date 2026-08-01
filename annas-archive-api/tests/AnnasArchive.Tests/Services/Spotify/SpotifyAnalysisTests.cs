using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Pure arithmetic over already-fetched contents. The recurring rule under test is
/// that a playlist we could not read is excluded from every conclusion rather than
/// counted as empty — a cleanup suggestion built on a partial view is worse than none.
/// </summary>
public class SpotifyAnalysisTests
{
    // ─── empty ───────────────────────────────────────────────────────────────

    [Fact]
    public void FindsPlaylistsSpotifyConfirmedAreEmpty()
    {
        var analysis = SpotifyAnalysis.Analyze([Readable("a", "Empty"), Readable("b", "Full", Track("x"))]);

        analysis.Empty.Should().ContainSingle().Which.Name.Should().Be("Empty");
    }

    [Theory]
    [InlineData(SpotifyContentsAccess.Forbidden)]
    [InlineData(SpotifyContentsAccess.Unavailable)]
    public void NeverCallsAnUnreadablePlaylistEmpty(SpotifyContentsAccess access)
    {
        // The whole point. An unreadable playlist in a delete list is data loss.
        var analysis = SpotifyAnalysis.Analyze([Unreadable("a", "Cannot Read", access)]);

        analysis.Empty.Should().BeEmpty();
        analysis.Unreadable.Should().ContainSingle();
        analysis.PlaylistsRead.Should().Be(0);
        analysis.PlaylistsScanned.Should().Be(1);
    }

    // ─── duplicate items within a playlist ───────────────────────────────────

    [Fact]
    public void FindsTheSameSongAddedTwiceToOnePlaylist()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Repeats", Track("Song", uri: "spotify:track:1"), Track("Other", uri: "spotify:track:2"),
                     Track("Song", uri: "spotify:track:1"))
        ]);

        var duplicate = analysis.DuplicateItems.Should().ContainSingle().Subject;
        duplicate.Confidence.Should().Be(SpotifyDuplicateConfidence.Exact);
        duplicate.Positions.Should().Equal(0, 2);
    }

    [Fact]
    public void FlagsTheSameRecordingUnderDifferentUrisAsProbableOnly()
    {
        // Different Spotify URIs for the same artist and title — usually a remaster,
        // sometimes a genuinely different recording. Never auto-selected for removal.
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Maybe",
                Track("Mystery Train", artists: "Elvis Presley", uri: "spotify:track:1"),
                Track("mystery train", artists: "elvis presley", uri: "spotify:track:2"))
        ]);

        analysis.DuplicateItems.Should().ContainSingle()
            .Which.Confidence.Should().Be(SpotifyDuplicateConfidence.Probable);
    }

    [Fact]
    public void DoesNotReportTheSameRepeatAsBothExactAndProbable()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Repeats",
                Track("Song", artists: "Artist", uri: "spotify:track:1"),
                Track("Song", artists: "Artist", uri: "spotify:track:1"))
        ]);

        analysis.DuplicateItems.Should().ContainSingle();
    }

    [Fact]
    public void DoesNotTreatTwoDifferentSongsAsDuplicates()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Fine", Track("One", uri: "spotify:track:1"), Track("Two", uri: "spotify:track:2"))
        ]);

        analysis.DuplicateItems.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotCollapseDifferentArtistsCoveringTheSameTitle()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Covers",
                Track("Hallelujah", artists: "Leonard Cohen", uri: "spotify:track:1"),
                Track("Hallelujah", artists: "Jeff Buckley", uri: "spotify:track:2"))
        ]);

        analysis.DuplicateItems.Should().BeEmpty();
    }

    [Fact]
    public void IgnoresLocalFilesWithNoUriWhenLookingForExactDuplicates()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Local", Local("Home Recording"), Local("Home Recording"))
        ]);

        analysis.DuplicateItems.Should().NotContain(d => d.Confidence == SpotifyDuplicateConfidence.Exact);
    }

    // ─── overlapping playlists ───────────────────────────────────────────────

    [Fact]
    public void FindsTwoPlaylistsWithIdenticalContents()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "One", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2")),
            Readable("b", "Two", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2"))
        ]);

        var overlap = analysis.OverlappingPlaylists.Should().ContainSingle().Subject;
        overlap.Identical.Should().BeTrue();
        overlap.SharedItems.Should().Be(2);
        overlap.LeftOnlyItems.Should().Be(0);
    }

    [Fact]
    public void TreatsDifferentOrderingAsIdentical()
    {
        // Sets, not sequences. Reordering a playlist does not make it a new one.
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "One", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2")),
            Readable("b", "Two", Track("y", uri: "spotify:track:2"), Track("x", uri: "spotify:track:1"))
        ]);

        analysis.OverlappingPlaylists.Should().ContainSingle().Which.Identical.Should().BeTrue();
    }

    [Fact]
    public void ReportsWhenOnePlaylistFullyContainsAnother()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("big", "Big", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2"),
                     Track("z", uri: "spotify:track:3")),
            Readable("small", "Small", Track("x", uri: "spotify:track:1"))
        ]);

        var overlap = analysis.OverlappingPlaylists.Should().ContainSingle().Subject;
        overlap.SupersetOf.Should().Be("small");
        overlap.Identical.Should().BeFalse();
    }

    [Fact]
    public void ReportsASupersetEvenWhenTheJaccardScoreIsWellBelowTheThreshold()
    {
        // One song inside a hundred scores ~0.01, but "this is already in that" is
        // exactly the cleanup signal the user wants.
        var big = Enumerable.Range(0, 100).Select(i => Track($"t{i}", uri: $"spotify:track:{i}")).ToArray();

        var analysis = SpotifyAnalysis.Analyze([
            Readable("big", "Big", big),
            Readable("small", "Small", Track("t0", uri: "spotify:track:0"))
        ]);

        analysis.OverlappingPlaylists.Should().ContainSingle().Which.SupersetOf.Should().Be("small");
    }

    [Fact]
    public void IgnoresPairsThatShareTooLittleToMatter()
    {
        var left = Enumerable.Range(0, 10).Select(i => Track($"t{i}", uri: $"spotify:track:{i}")).ToArray();
        var right = Enumerable.Range(5, 10).Select(i => Track($"t{i}", uri: $"spotify:track:{i}")).ToArray();

        // 5 shared of 15 combined ≈ 0.33, below the 0.85 default and not a superset.
        SpotifyAnalysis.Analyze([Readable("a", "A", left), Readable("b", "B", right)])
            .OverlappingPlaylists.Should().BeEmpty();
    }

    [Fact]
    public void ExcludesUnreadablePlaylistsFromOverlapEntirely()
    {
        // An unreadable playlist has an empty item set, which would otherwise look
        // like a subset of everything.
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Readable", Track("x", uri: "spotify:track:1")),
            Unreadable("b", "Hidden", SpotifyContentsAccess.Forbidden)
        ]);

        analysis.OverlappingPlaylists.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotComparePlaylistsWithItself()
    {
        SpotifyAnalysis.Analyze([Readable("a", "Only", Track("x", uri: "spotify:track:1"))])
            .OverlappingPlaylists.Should().BeEmpty();
    }

    [Fact]
    public void OrdersTheMostSimilarPairFirst()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "A", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2")),
            Readable("b", "B", Track("x", uri: "spotify:track:1"), Track("y", uri: "spotify:track:2")),
            Readable("c", "C", Track("x", uri: "spotify:track:1"))
        ]);

        analysis.OverlappingPlaylists[0].Overlap.Should().Be(1.0);
    }

    // ─── naming collisions ───────────────────────────────────────────────────

    [Fact]
    public void FindsNamesThatDifferOnlyByPunctuationOrCase()
    {
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Road Trip"), Readable("b", "road-trip"), Readable("c", "Dinner")
        ]);

        analysis.NamingCollisions.Should().ContainSingle().Which.Playlists.Should().HaveCount(2);
    }

    [Fact]
    public void IncludesUnreadablePlaylistsInNamingCollisions()
    {
        // A name clash is visible without reading contents, and it is precisely when
        // the assistant has to ask which one you meant.
        var analysis = SpotifyAnalysis.Analyze([
            Readable("a", "Chill"), Unreadable("b", "chill", SpotifyContentsAccess.Forbidden)
        ]);

        analysis.NamingCollisions.Should().ContainSingle();
    }

    [Fact]
    public void DoesNotReportDistinctNamesAsColliding()
    {
        SpotifyAnalysis.Analyze([Readable("a", "Road Trip 2025"), Readable("b", "Road Trip 2026")])
            .NamingCollisions.Should().BeEmpty();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static SpotifyPlaylistContents Readable(
        string id, string name, params SpotifyPlaylistItemDto[] items) =>
        new(new SpotifyPlaylistDto(id, name, null, items.Length, null, IsOwnedByUser: true),
            items.Select((item, index) => item with { Position = index }).ToList(),
            SpotifyContentsAccess.Available, $"snap-{id}");

    private static SpotifyPlaylistContents Unreadable(string id, string name, SpotifyContentsAccess access) =>
        new(new SpotifyPlaylistDto(id, name, null, null, null, ContentsAvailable: false),
            [], access, $"snap-{id}");

    private static SpotifyPlaylistItemDto Track(string name, string artists = "Artist", string? uri = null) =>
        new(0, SpotifyItemKind.Track, "id", name, uri ?? $"spotify:track:{name}", artists,
            "Album", 1000, null, false, null);

    private static SpotifyPlaylistItemDto Local(string name) =>
        new(0, SpotifyItemKind.Local, null, name, null, "Artist", null, 1000, null, true, null);
}
