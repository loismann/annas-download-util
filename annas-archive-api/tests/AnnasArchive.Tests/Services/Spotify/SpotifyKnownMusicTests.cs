using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Spotify cannot prove anyone has never heard a song. This index only ever
/// supports "does not appear in the data I can access", so the tests care as much
/// about the coverage caveat travelling with the answer as about the lookup itself.
/// </summary>
public class SpotifyKnownMusicTests
{
    [Fact]
    public void IndexesArtistsAndTracksFromReadablePlaylists()
    {
        var index = SpotifyKnownMusic.Build([Readable("a", Track("Mystery Train", "Elvis Presley"))]);

        index.IsArtistAbsent("Elvis Presley").Should().BeFalse();
        index.IsTrackAbsent("Elvis Presley", "Mystery Train").Should().BeFalse();
    }

    [Fact]
    public void TreatsUnseenMusicAsAbsent()
    {
        var index = SpotifyKnownMusic.Build([Readable("a", Track("Mystery Train", "Elvis Presley"))]);

        index.IsArtistAbsent("Skip James").Should().BeTrue();
    }

    [Fact]
    public void IndexesEachArtistOfACollaborationSeparately()
    {
        // Indexing only the joined "A, B" string would miss both of them.
        var index = SpotifyKnownMusic.Build([Readable("a", Track("Song", "Robert Johnson, Son House"))]);

        index.IsArtistAbsent("Robert Johnson").Should().BeFalse();
        index.IsArtistAbsent("Son House").Should().BeFalse();
    }

    [Fact]
    public void MatchesThroughCaseAndPunctuation()
    {
        var index = SpotifyKnownMusic.Build([Readable("a", Track("Mystery Train", "Elvis Presley"))]);

        index.IsArtistAbsent("elvis  presley!").Should().BeFalse();
    }

    [Fact]
    public void IgnoresPlaylistsItCouldNotRead()
    {
        var index = SpotifyKnownMusic.Build([Unreadable("a")]);

        index.PlaylistsIncluded.Should().Be(0);
        index.UnreadablePlaylists.Should().Be(1);
    }

    [Fact]
    public void IgnoresPodcastEpisodesAndRemovedItems()
    {
        // Neither says anything about musical familiarity.
        var index = SpotifyKnownMusic.Build([
            Readable("a",
                new SpotifyPlaylistItemDto(0, SpotifyItemKind.Episode, "e", "Some Episode", "spotify:episode:e",
                    "", null, 1000, null, false, null),
                new SpotifyPlaylistItemDto(1, SpotifyItemKind.Unavailable, null, null, null,
                    "", null, 0, null, false, null))
        ]);

        index.TrackKeys.Should().BeEmpty();
    }

    [Fact]
    public void IncludesTopTracksAndTheirArtists()
    {
        var top = new SpotifyTopItemsDto("tracks", "medium_term",
            [new SpotifyTopItemDto("t1", "Cross Road Blues", "Robert Johnson", null, 1)]);

        var index = SpotifyKnownMusic.Build([], topTracks: top);

        index.IsArtistAbsent("Robert Johnson").Should().BeFalse();
        index.IsTrackAbsent("Robert Johnson", "Cross Road Blues").Should().BeFalse();
        index.IncludesTopItems.Should().BeTrue();
    }

    [Fact]
    public void IncludesTopArtists()
    {
        var top = new SpotifyTopItemsDto("artists", "long_term",
            [new SpotifyTopItemDto("a1", "Skip James", "delta blues", null, 1)]);

        SpotifyKnownMusic.Build([], topArtists: top).IsArtistAbsent("Skip James").Should().BeFalse();
    }

    [Fact]
    public void IncludesRecentlyPlayedTracks()
    {
        var index = SpotifyKnownMusic.Build([], recentTracks: [Track("Walkin' Blues", "Son House")]);

        index.IsArtistAbsent("Son House").Should().BeFalse();
        index.IncludesRecentHistory.Should().BeTrue();
    }

    // ─── honesty about coverage ──────────────────────────────────────────────

    [Fact]
    public void SaysWhatItLookedAt()
    {
        var index = SpotifyKnownMusic.Build(
            [Readable("a", Track("Song", "Artist"))],
            topTracks: new SpotifyTopItemsDto("tracks", "medium_term", []),
            recentTracks: [Track("Other", "Someone")]);

        var coverage = index.DescribeCoverage();

        coverage.Should().Contain("1 readable playlists")
            .And.Contain("top artists and tracks")
            .And.Contain("recent listening");
    }

    [Fact]
    public void WarnsThatTheAnswerIsPartialWhenPlaylistsCouldNotBeRead()
    {
        var index = SpotifyKnownMusic.Build([Readable("a", Track("Song", "Artist")), Unreadable("b")]);

        index.DescribeCoverage().Should().Contain("partial picture")
            .And.Contain("not proof");
    }

    [Fact]
    public void DoesNotClaimPartialCoverageWhenEverythingWasReadable()
    {
        SpotifyKnownMusic.Build([Readable("a", Track("Song", "Artist"))])
            .DescribeCoverage().Should().NotContain("partial picture");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DoesNotReportABlankArtistAsAbsent(string? artist)
    {
        // "Nothing" is not an artist you have not heard of.
        SpotifyKnownMusic.Build([]).IsArtistAbsent(artist).Should().BeFalse();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static SpotifyPlaylistContents Readable(string id, params SpotifyPlaylistItemDto[] items) =>
        new(new SpotifyPlaylistDto(id, id, null, items.Length, null, IsOwnedByUser: true),
            items, SpotifyContentsAccess.Available, $"snap-{id}");

    private static SpotifyPlaylistContents Unreadable(string id) =>
        new(new SpotifyPlaylistDto(id, id, null, null, null, ContentsAvailable: false),
            [], SpotifyContentsAccess.Forbidden, null);

    private static SpotifyPlaylistItemDto Track(string name, string artists) =>
        new(0, SpotifyItemKind.Track, "t", name, $"spotify:track:{name}", artists,
            "Album", 1000, null, false, null);
}
