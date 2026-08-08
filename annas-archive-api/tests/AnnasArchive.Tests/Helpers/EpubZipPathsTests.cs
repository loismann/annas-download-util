using System.Text;
using AnnasArchive.API.Helpers;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// These were private statics inside an 840-line file that also does file I/O, HTTP and
/// zip repair, so none of them had ever been run directly.
///
/// Every case here is a way a real EPUB's manifest fails to name its own entries
/// exactly — URL encoding, backslashes, fragments, case, a missing container directory.
/// They all fail the same silent way: a chapter that opens empty.
/// </summary>
public class EpubZipPathsTests
{
    // ─── NormalizeZipPath ─────────────────────────────────────────────────

    [Theory]
    [InlineData("OEBPS/ch1.xhtml", "OEBPS/ch1.xhtml")]
    [InlineData("OEBPS\\ch1.xhtml", "OEBPS/ch1.xhtml")]   // authored on Windows
    [InlineData("/OEBPS/ch1.xhtml", "OEBPS/ch1.xhtml")]   // absolute-looking
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeZipPath_PutsEveryPathInOneComparableForm(string input, string expected) =>
        EpubZipPaths.NormalizeZipPath(input).Should().Be(expected);

    /// <summary>
    /// A spine href routinely points at an anchor inside a document. Keeping the
    /// fragment would mean looking up a zip entry that cannot exist.
    /// </summary>
    [Fact]
    public void NormalizeZipPath_DropsTheFragmentThatNamesAPositionNotAFile()
    {
        EpubZipPaths.NormalizeZipPath("OEBPS/ch1.xhtml#section-3").Should().Be("OEBPS/ch1.xhtml");
    }

    [Fact]
    public void NormalizeZipPath_HandlesAnHrefThatIsOnlyAFragment()
    {
        EpubZipPaths.NormalizeZipPath("#top").Should().BeEmpty();
    }

    [Theory]
    [InlineData("OEBPS/", "OEBPS")]
    [InlineData("OEBPS", "OEBPS")]
    [InlineData("/OEBPS/text/", "OEBPS/text")]
    [InlineData("", "")]
    public void NormalizeZipDir_LeavesNoTrailingSlashToDoubleUp(string input, string expected) =>
        EpubZipPaths.NormalizeZipDir(input).Should().Be(expected);

    // ─── ResolveOpfHref ───────────────────────────────────────────────────

    [Fact]
    public void ResolveOpfHref_JoinsAnHrefToTheOpfsOwnDirectory()
    {
        EpubZipPaths.ResolveOpfHref("OEBPS", "text/ch1.xhtml").Should().Be("OEBPS/text/ch1.xhtml");
    }

    /// <summary>
    /// Manifest hrefs are URL-encoded, so a chapter named "Part 1.xhtml" appears as
    /// "Part%201.xhtml". Looking that up verbatim finds nothing.
    /// </summary>
    [Fact]
    public void ResolveOpfHref_DecodesThePercentEncodingTheManifestUses()
    {
        EpubZipPaths.ResolveOpfHref("OEBPS", "Part%201.xhtml").Should().Be("OEBPS/Part 1.xhtml");
    }

    [Fact]
    public void ResolveOpfHref_DecodesAnEncodedAmpersand()
    {
        EpubZipPaths.ResolveOpfHref("OEBPS", "Crime%20%26%20Punishment.xhtml")
            .Should().Be("OEBPS/Crime & Punishment.xhtml");
    }

    /// <summary>An OPF at the archive root has no directory to resolve against.</summary>
    [Fact]
    public void ResolveOpfHref_ReturnsTheHrefAloneWhenTheOpfIsAtTheRoot()
    {
        EpubZipPaths.ResolveOpfHref("", "ch1.xhtml").Should().Be("ch1.xhtml");
    }

    [Fact]
    public void ResolveOpfHref_NormalisesBackslashesInTheHref()
    {
        EpubZipPaths.ResolveOpfHref("OEBPS", "text\\ch1.xhtml").Should().Be("OEBPS/text/ch1.xhtml");
    }

    // ─── FindEntry ────────────────────────────────────────────────────────

    private static Dictionary<string, byte[]> Entries(params string[] keys) =>
        keys.ToDictionary(k => k, _ => Array.Empty<byte>());

    [Fact]
    public void FindEntry_PrefersAnExactMatch()
    {
        var entries = Entries("OEBPS/ch1.xhtml", "extra/OEBPS/ch1.xhtml");

        EpubZipPaths.FindEntry(entries, "OEBPS/ch1.xhtml").Should().Be("OEBPS/ch1.xhtml");
    }

    /// <summary>
    /// The fallback that makes real books work: a manifest naming "ch1.xhtml" against an
    /// archive that stores it under "OEBPS/". Without it the chapter resolves to nothing.
    /// </summary>
    [Fact]
    public void FindEntry_FallsBackToAnEntryEndingWithTheHref()
    {
        var entries = Entries("OEBPS/text/ch1.xhtml");

        EpubZipPaths.FindEntry(entries, "ch1.xhtml").Should().Be("OEBPS/text/ch1.xhtml");
    }

    /// <summary>Zip entries and manifest hrefs disagree on case often enough to matter.</summary>
    [Fact]
    public void FindEntry_MatchesTheSuffixWithoutRegardToCase()
    {
        var entries = Entries("OEBPS/Text/Ch1.XHTML");

        EpubZipPaths.FindEntry(entries, "ch1.xhtml").Should().Be("OEBPS/Text/Ch1.XHTML");
    }

    [Fact]
    public void FindEntry_ResolvesAnHrefCarryingAFragment()
    {
        var entries = Entries("OEBPS/ch1.xhtml");

        EpubZipPaths.FindEntry(entries, "OEBPS/ch1.xhtml#part2").Should().Be("OEBPS/ch1.xhtml");
    }

    [Fact]
    public void FindEntry_ReturnsNullWhenNothingResembles()
    {
        EpubZipPaths.FindEntry(Entries("OEBPS/ch1.xhtml"), "missing.xhtml").Should().BeNull();
    }

    // ─── IsHtmlEntry ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ch1.xhtml", true)]
    [InlineData("ch1.html", true)]
    [InlineData("ch1.htm", true)]
    [InlineData("CH1.XHTML", true)]
    [InlineData("cover.jpg", false)]
    [InlineData("style.css", false)]
    [InlineData("font.otf", false)]
    [InlineData("content.opf", false)]
    [InlineData("", false)]
    public void IsHtmlEntry_SelectsDocumentsAndNothingElse(string path, bool expected) =>
        EpubZipPaths.IsHtmlEntry(path).Should().Be(expected);

    // ─── ReadTextFromBytes ────────────────────────────────────────────────

    [Fact]
    public void ReadTextFromBytes_ReadsPlainUtf8()
    {
        EpubZipPaths.ReadTextFromBytes(Encoding.UTF8.GetBytes("<h1>Chapter</h1>"))
            .Should().Be("<h1>Chapter</h1>");
    }

    /// <summary>
    /// A BOM left in the string becomes an invisible leading character that breaks the
    /// very first regex match against the document.
    /// </summary>
    [Fact]
    public void ReadTextFromBytes_ConsumesAByteOrderMarkRatherThanKeepingIt()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<h1>Ch</h1>")).ToArray();

        EpubZipPaths.ReadTextFromBytes(withBom).Should().Be("<h1>Ch</h1>");
    }

    [Fact]
    public void ReadTextFromBytes_HandlesAnEmptyEntry()
    {
        EpubZipPaths.ReadTextFromBytes([]).Should().BeEmpty();
    }

    // ─── ExtractTitleFromHtml ─────────────────────────────────────────────

    [Fact]
    public void ExtractTitleFromHtml_PrefersTheTitleElement()
    {
        EpubZipPaths.ExtractTitleFromHtml("<html><head><title>Chapter One</title></head><body><h1>Ignored</h1></body></html>")
            .Should().Be("Chapter One");
    }

    /// <summary>
    /// Plenty of EPUBs ship an empty <c>&lt;title/&gt;</c> and carry the real one in the
    /// body, so an empty match must not count as an answer.
    /// </summary>
    [Fact]
    public void ExtractTitleFromHtml_FallsThroughAnEmptyTitleToTheHeading()
    {
        EpubZipPaths.ExtractTitleFromHtml("<head><title>   </title></head><body><h1>Real Title</h1></body>")
            .Should().Be("Real Title");
    }

    [Fact]
    public void ExtractTitleFromHtml_FallsThroughToAnH2()
    {
        EpubZipPaths.ExtractTitleFromHtml("<body><h2>Second Level</h2></body>").Should().Be("Second Level");
    }

    [Fact]
    public void ExtractTitleFromHtml_DecodesEntitiesSoTheTitleReadsAsWritten()
    {
        EpubZipPaths.ExtractTitleFromHtml("<title>Crime &amp; Punishment</title>")
            .Should().Be("Crime & Punishment");
    }

    /// <summary>Titles are routinely split across lines by the EPUB's own formatter.</summary>
    [Fact]
    public void ExtractTitleFromHtml_MatchesATitleBrokenOverSeveralLines()
    {
        EpubZipPaths.ExtractTitleFromHtml("<title>\n  Chapter\n  One\n</title>")
            .Should().Be("Chapter\n  One");
    }

    [Fact]
    public void ExtractTitleFromHtml_ReadsThroughAttributesOnTheTag()
    {
        EpubZipPaths.ExtractTitleFromHtml("<h1 class=\"chapter-title\" id=\"c1\">Titled</h1>")
            .Should().Be("Titled");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<body><p>No heading at all.</p></body>")]
    public void ExtractTitleFromHtml_ReturnsNullWhenThereIsNoTitle(string html) =>
        EpubZipPaths.ExtractTitleFromHtml(html).Should().BeNull();

    // ─── ExtractMissingEpubPath ───────────────────────────────────────────

    [Fact]
    public void ExtractMissingEpubPath_ReadsAQuotedPath()
    {
        EpubZipPaths.ExtractMissingEpubPath("The file \"OEBPS/missing.xhtml\" was not found in the archive")
            .Should().Be("OEBPS/missing.xhtml");
    }

    /// <summary>The reader's message uses curly quotes on some inputs.</summary>
    [Fact]
    public void ExtractMissingEpubPath_ReadsAPathInCurlyQuotes()
    {
        EpubZipPaths.ExtractMissingEpubPath("The file “OEBPS/missing.xhtml” was not found")
            .Should().Be("OEBPS/missing.xhtml");
    }

    [Fact]
    public void ExtractMissingEpubPath_ReadsAnUnquotedPath()
    {
        EpubZipPaths.ExtractMissingEpubPath("The file OEBPS/missing.xhtml was not found")
            .Should().Be("OEBPS/missing.xhtml");
    }

    /// <summary>
    /// The last resort, for messages that name a path without the "was not found"
    /// phrasing at all. Without it those exceptions carry no recoverable path and the
    /// repair step cannot run.
    /// </summary>
    [Fact]
    public void ExtractMissingEpubPath_FallsBackToAnyOebpsPathInTheMessage()
    {
        EpubZipPaths.ExtractMissingEpubPath("Unexpected failure reading OEBPS/text/ch3.xhtml during parse")
            .Should().Be("OEBPS/text/ch3.xhtml");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Something else went wrong entirely")]
    public void ExtractMissingEpubPath_ReturnsNullWhenNoPathIsRecoverable(string message) =>
        EpubZipPaths.ExtractMissingEpubPath(message).Should().BeNull();

    // ─── ComputeHash ──────────────────────────────────────────────────────

    /// <summary>
    /// The cache directory name. Stability matters more than the value: a change here
    /// silently orphans every cached book on disk.
    /// </summary>
    [Fact]
    public void ComputeHash_IsStableLowercaseHexOfTheSourceKey()
    {
        var hash = EpubChapterCache.ComputeHash("dropbox:/Books/Dune.epub");

        hash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        hash.Should().Be(EpubChapterCache.ComputeHash("dropbox:/Books/Dune.epub"));
    }

    [Fact]
    public void ComputeHash_SeparatesTwoBooksThatDifferByOneCharacter()
    {
        EpubChapterCache.ComputeHash("dropbox:/Books/Dune.epub")
            .Should().NotBe(EpubChapterCache.ComputeHash("dropbox:/Books/dune.epub"));
    }
}
