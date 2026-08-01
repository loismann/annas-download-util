using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Playlist names are not unique in Spotify, so the one behaviour that must never
/// appear here is a guess. Every test that ends in Ambiguous is asserting that the
/// resolver refused to pick.
/// </summary>
public class SpotifyPlaylistResolverTests
{
    [Fact]
    public void ResolvesAnExactNameMatch()
    {
        var result = SpotifyPlaylistResolver.Resolve("Road Trip", [Owned("a", "Road Trip"), Owned("b", "Dinner")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
        result.Playlist!.Id.Should().Be("a");
    }

    [Theory]
    [InlineData("road trip")]
    [InlineData("ROAD TRIP")]
    [InlineData("  Road Trip  ")]
    public void IgnoresCaseAndSurroundingWhitespace(string reference)
    {
        var result = SpotifyPlaylistResolver.Resolve(reference, [Owned("a", "Road Trip")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
    }

    // ─── ownership tiers ─────────────────────────────────────────────────────

    [Fact]
    public void PrefersAPlaylistYouOwnOverAnIdenticallyNamedOneYouFollow()
    {
        // Someone else's public "Chill" must not shadow your own.
        var result = SpotifyPlaylistResolver.Resolve("Chill", [Followed("theirs", "Chill"), Owned("mine", "Chill")]);

        result.Playlist!.Id.Should().Be("mine");
        result.MatchedBy.Should().Contain("owned");
    }

    [Fact]
    public void PrefersACollaborativePlaylistOverAFollowedOne()
    {
        var result = SpotifyPlaylistResolver.Resolve(
            "Chill", [Followed("theirs", "Chill"), Collaborative("ours", "Chill")]);

        result.Playlist!.Id.Should().Be("ours");
    }

    [Fact]
    public void FallsAllTheWayToAFollowedPlaylistWhenNothingCloserExists()
    {
        var result = SpotifyPlaylistResolver.Resolve("Chill", [Followed("theirs", "Chill")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
        result.Playlist!.Id.Should().Be("theirs");
    }

    // ─── ambiguity is an answer, not an error ────────────────────────────────

    [Fact]
    public void ReturnsEveryCandidateWhenTwoPlaylistsYouOwnShareAName()
    {
        var result = SpotifyPlaylistResolver.Resolve("Chill", [Owned("one", "Chill"), Owned("two", "Chill")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Ambiguous);
        result.Candidates.Should().HaveCount(2);
        result.Playlist.Should().BeNull();
    }

    [Fact]
    public void DoesNotFallThroughToALowerTierAfterAnAmbiguousHit()
    {
        // Two owned "Chill" playlists is a question for the user. Quietly moving on
        // to the followed tier would answer a question they were never asked.
        var result = SpotifyPlaylistResolver.Resolve(
            "Chill", [Owned("one", "Chill"), Owned("two", "Chill"), Followed("three", "Chill")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Ambiguous);
        result.Candidates.Should().HaveCount(2);
    }

    // ─── normalised and partial matching ─────────────────────────────────────

    [Theory]
    [InlineData("Road Trip 2026", "Road Trip — 2026")]
    [InlineData("road-trip-2026", "Road Trip 2026")]
    [InlineData("Cafe", "Café")]
    public void MatchesThroughPunctuationSpacingAndAccents(string reference, string playlistName)
    {
        var result = SpotifyPlaylistResolver.Resolve(reference, [Owned("a", playlistName)]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
    }

    [Fact]
    public void AcceptsAPartialNameOnlyWhenItLeavesExactlyOneCandidate()
    {
        var result = SpotifyPlaylistResolver.Resolve("Trip", [Owned("a", "Road Trip 2026"), Owned("b", "Dinner")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
        result.Playlist!.Id.Should().Be("a");
    }

    [Fact]
    public void RefusesAPartialNameThatCouldMeanEitherPlaylist()
    {
        var result = SpotifyPlaylistResolver.Resolve(
            "Trip", [Owned("a", "Road Trip 2025"), Owned("b", "Road Trip 2026")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Ambiguous);
        result.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void PrefersAnExactNameOverALongerPlaylistThatContainsIt()
    {
        // "Road Trip" exists outright, so the substring hit on "Road Trip 2026"
        // must not turn a certain answer into an ambiguous one.
        var result = SpotifyPlaylistResolver.Resolve(
            "Road Trip", [Owned("exact", "Road Trip"), Owned("longer", "Road Trip 2026")]);

        result.Kind.Should().Be(SpotifyPlaylistMatchKind.Resolved);
        result.Playlist!.Id.Should().Be("exact");
    }

    // ─── nothing to resolve ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReportsNotFoundForABlankReference(string? reference)
    {
        SpotifyPlaylistResolver.Resolve(reference, [Owned("a", "Road Trip")])
            .Kind.Should().Be(SpotifyPlaylistMatchKind.NotFound);
    }

    [Fact]
    public void ReportsNotFoundWhenNothingMatches()
    {
        SpotifyPlaylistResolver.Resolve("Nonexistent", [Owned("a", "Road Trip")])
            .Kind.Should().Be(SpotifyPlaylistMatchKind.NotFound);
    }

    [Fact]
    public void ReportsNotFoundWhenTheAccountHasNoPlaylists()
    {
        SpotifyPlaylistResolver.Resolve("Anything", [])
            .Kind.Should().Be(SpotifyPlaylistMatchKind.NotFound);
    }

    // ─── Filter ──────────────────────────────────────────────────────────────

    [Fact]
    public void FilterKeepsEveryPlaylistWhoseNameContainsTheQuery()
    {
        var matches = SpotifyPlaylistResolver.Filter(
            "best of", [Owned("a", "Best Of 2024"), Owned("b", "Best of 2025"), Owned("c", "Dinner")]);

        matches.Should().HaveCount(2);
    }

    [Fact]
    public void FilterIgnoresPunctuationInBothDirections()
    {
        var matches = SpotifyPlaylistResolver.Filter("best-of", [Owned("a", "Best Of 2024")]);

        matches.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FilterReturnsEverythingForABlankQuery(string? query)
    {
        SpotifyPlaylistResolver.Filter(query, [Owned("a", "One"), Owned("b", "Two")]).Should().HaveCount(2);
    }

    [Fact]
    public void FilterReturnsEmptyRatherThanEverythingWhenNothingMatches()
    {
        SpotifyPlaylistResolver.Filter("zzz", [Owned("a", "One")]).Should().BeEmpty();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static SpotifyPlaylistDto Owned(string id, string name) =>
        new(id, name, null, 0, null, IsOwnedByUser: true);

    private static SpotifyPlaylistDto Collaborative(string id, string name) =>
        new(id, name, null, 0, null, IsOwnedByUser: false, IsCollaborative: true);

    private static SpotifyPlaylistDto Followed(string id, string name) =>
        new(id, name, null, 0, null, IsOwnedByUser: false);
}
