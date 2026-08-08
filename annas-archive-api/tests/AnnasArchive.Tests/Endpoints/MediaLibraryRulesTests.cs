using AnnasArchive.API.Endpoints;

namespace AnnasArchive.Tests.Endpoints;

/// <summary>
/// Both of these were private statics in an 827-line endpoint file. The playlist
/// rewriter is the one that matters most: it puts an access token into every segment
/// URL a video player will request, and it has to leave the playlist's own structure
/// alone while doing it.
/// </summary>
public class MediaLibraryRulesTests
{
    // ─── RewriteHlsPlaylist ───────────────────────────────────────────────

    private const string Token = "tok123";

    private static string Rewrite(string playlist, string itemId = "abc") =>
        MediaLibraryRules.RewriteHlsPlaylist(playlist, itemId, Token);

    /// <summary>
    /// The point of the whole function: the player fetches segments itself, with no
    /// Authorization header, so a segment URL without a token is an anonymous request
    /// that gets rejected.
    /// </summary>
    [Fact]
    public void RewriteHlsPlaylist_RoutesEachSegmentBackThroughThisApiWithAToken()
    {
        Rewrite("segment0.ts").Should().Be("/api/media/hls/abc/segment0.ts?access_token=tok123");
    }

    /// <summary>Comment lines are the playlist's structure. Rewriting one breaks parsing.</summary>
    [Fact]
    public void RewriteHlsPlaylist_LeavesDirectiveLinesExactlyAsTheyWere()
    {
        var playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.0,\nsegment0.ts\n#EXT-X-ENDLIST";

        Rewrite(playlist).Should().Be(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.0,\n" +
            "/api/media/hls/abc/segment0.ts?access_token=tok123\n#EXT-X-ENDLIST");
    }

    /// <summary>
    /// A segment URI may already carry query parameters, so the token has to be
    /// appended rather than started — a second '?' makes the whole query unparseable.
    /// </summary>
    [Fact]
    public void RewriteHlsPlaylist_AppendsToAnExistingQueryStringInsteadOfStartingASecond()
    {
        Rewrite("segment0.ts?runtimeTicks=0")
            .Should().Be("/api/media/hls/abc/segment0.ts?runtimeTicks=0&access_token=tok123");
    }

    /// <summary>
    /// Playlists arrive with CRLF line endings. A surviving '\r' would land in the
    /// middle of the rewritten URL, where it is invisible and breaks every request.
    /// </summary>
    [Fact]
    public void RewriteHlsPlaylist_StripsTheCarriageReturnBeforeBuildingTheUrl()
    {
        Rewrite("#EXTM3U\r\nsegment0.ts\r\n")
            .Should().Be("#EXTM3U\n/api/media/hls/abc/segment0.ts?access_token=tok123\n");
    }

    [Fact]
    public void RewriteHlsPlaylist_PreservesBlankLines()
    {
        Rewrite("#EXTM3U\n\nsegment0.ts")
            .Should().Be("#EXTM3U\n\n/api/media/hls/abc/segment0.ts?access_token=tok123");
    }

    /// <summary>A token is credential material and goes into a URL, so it must be encoded.</summary>
    [Fact]
    public void RewriteHlsPlaylist_EscapesATokenContainingUrlSignificantCharacters()
    {
        MediaLibraryRules.RewriteHlsPlaylist("segment0.ts", "abc", "a+b/c=d&e")
            .Should().Be("/api/media/hls/abc/segment0.ts?access_token=a%2Bb%2Fc%3Dd%26e");
    }

    [Fact]
    public void RewriteHlsPlaylist_RewritesEverySegmentNotJustTheFirst()
    {
        var result = Rewrite("seg0.ts\nseg1.ts\nseg2.ts");

        result.Split('\n').Should().OnlyContain(l => l.Contains("access_token=tok123"));
    }

    [Fact]
    public void RewriteHlsPlaylist_HandlesAnEmptyPlaylist()
    {
        Rewrite("").Should().BeEmpty();
    }

    // ─── ValidateMetadata ─────────────────────────────────────────────────

    private static SetMediaMetadataRequest Request(List<string>? owners, List<string>? genres = null) =>
        new(owners, genres);

    [Fact]
    public void ValidateMetadata_AcceptsTheHouseholdMembers()
    {
        var result = MediaLibraryRules.ValidateMetadata(Request(["Paul", "Mom", "Dad"]));

        result.Should().NotBeNull();
        result!.Owners.Should().Equal("Paul", "Mom", "Dad");
    }

    [Fact]
    public void ValidateMetadata_AcceptsAnOwnerRegardlessOfCasing()
    {
        MediaLibraryRules.ValidateMetadata(Request(["mom"]))!.Owners.Should().Equal("mom");
    }

    /// <summary>
    /// Rejecting the whole request rather than dropping the unknown name: saving a
    /// subset would report success for an edit that did not happen.
    /// </summary>
    [Fact]
    public void ValidateMetadata_RejectsTheWholeEditWhenAnOwnerIsNotInTheHousehold()
    {
        MediaLibraryRules.ValidateMetadata(Request(["Mom", "Stranger"])).Should().BeNull();
    }

    [Fact]
    public void ValidateMetadata_TrimsAndDropsBlankOwners()
    {
        var result = MediaLibraryRules.ValidateMetadata(Request(["  Mom  ", "", "   "]));

        result!.Owners.Should().Equal("Mom");
    }

    [Fact]
    public void ValidateMetadata_DeduplicatesOwnersWithoutRegardToCase()
    {
        MediaLibraryRules.ValidateMetadata(Request(["Mom", "mom", "MOM"]))!
            .Owners.Should().Equal("Mom");
    }

    /// <summary>Genres are free text — they are tidied, never rejected.</summary>
    [Fact]
    public void ValidateMetadata_TidiesGenresButAcceptsAnyValue()
    {
        var result = MediaLibraryRules.ValidateMetadata(
            Request(["Mom"], ["  Sci-Fi ", "sci-fi", "", "Noir"]));

        result!.Genres.Should().Equal("Sci-Fi", "Noir");
    }

    [Fact]
    public void ValidateMetadata_TreatsAbsentListsAsEmptyRatherThanFailing()
    {
        var result = MediaLibraryRules.ValidateMetadata(Request(null, null));

        result.Should().NotBeNull();
        result!.Owners.Should().BeEmpty();
        result.Genres.Should().BeEmpty();
    }

    /// <summary>Clearing every owner is a legitimate edit, not an invalid one.</summary>
    [Fact]
    public void ValidateMetadata_AllowsAnEditThatRemovesEveryOwner()
    {
        MediaLibraryRules.ValidateMetadata(Request([]))!.Owners.Should().BeEmpty();
    }
}
