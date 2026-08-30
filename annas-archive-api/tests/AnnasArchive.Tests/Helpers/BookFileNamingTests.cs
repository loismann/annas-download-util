using System.Net.Http;
using System.Net.Http.Headers;
using AnnasArchive.API.Helpers;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// What a downloaded book is called on disk — the shared namer behind both
/// <c>send-to-library</c> routes.
///
/// <para>The title comes from a third-party catalogue and becomes a filename, so
/// this is the point where untrusted text meets the filesystem. It is also where
/// Anna's and LibGen agree: the rule was written out twice and had already drifted,
/// so a book fetched from the fallback source was named by whichever copy the call
/// happened to land in.</para>
/// </summary>
public class BookFileNamingTests
{
    private const string Md5 = "abc123def456789012345678901234ab";

    private static HttpResponseMessage Served(string? contentType)
    {
        var response = new HttpResponseMessage { Content = new ByteArrayContent([1, 2, 3]) };
        if (contentType is not null)
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static string NameFor(string? title, string? url = "https://mirror.test/get/x.epub",
        string? contentType = "application/epub+zip") =>
        BookFileNaming.For(title, Md5, url, Served(contentType)).FileName;

    // ─── the title becomes a filename ─────────────────────────────────────

    [Fact]
    public void An_ordinary_title_keeps_its_shape()
    {
        NameFor("Dune").Should().Be("Dune.epub");
    }

    /// <summary>
    /// The property that matters: a title is text, never a path. It arrives from a
    /// catalogue nobody here controls and is joined to the library root, so a title
    /// that survived with a separator in it would write outside the library.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("sub/dir/Dune")]
    public void A_title_that_looks_like_a_path_never_produces_one(string title)
    {
        var name = NameFor(title);

        name.Should().NotContain("/").And.NotContain("\\");
        name.Should().NotContain("..");
        Path.GetFileName(name).Should().Be(name, "the whole name is a single leaf");
    }

    /// <summary>A null byte truncates a path in the C libraries underneath, so a title
    /// carrying one must not reach the filesystem intact.</summary>
    [Fact]
    public void Control_characters_do_not_reach_the_filesystem()
    {
        NameFor("Dune\0.txt").Should().Be("Dune.txt.epub");
    }

    /// <summary>
    /// The md5 is the fallback, and it is a good one: it is unique, it is already
    /// known to be a safe filename, and it identifies the book. Titles that sanitise
    /// away entirely are exactly the ones with nothing else to name them by.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void A_title_that_leaves_nothing_behind_falls_back_to_the_md5(string? title)
    {
        NameFor(title).Should().Be($"{Md5}.epub");
    }

    /// <summary>
    /// <b>A dots-only title does not reach the fallback</b>, and that is worth knowing
    /// rather than assuming otherwise.
    ///
    /// <para><c>ForUserInput</c> neutralises <c>".."</c> to <c>"_"</c> before it trims,
    /// so <c>"..."</c> survives as a bare <c>"_"</c> — a legal name, so the md5
    /// fallback never fires. Safe, but not identifying: two books titled this way both
    /// land on <c>_.epub</c> and the second overwrites the first.</para>
    ///
    /// <para><c>SafeFileName.ForReadablePathSegment</c> handles exactly this case and
    /// falls back, with a comment explaining why <c>"_"</c> tells a person less than
    /// <c>"untitled"</c>. <c>ForUserInput</c> was never given the same treatment. Not
    /// changed here: it is shared with the identity migration and the token-usage
    /// store, where what it returns is already written to disk under those names.</para>
    /// </summary>
    [Fact]
    public void A_dots_only_title_becomes_an_underscore_rather_than_the_md5()
    {
        NameFor("...").Should().Be("_.epub");
    }

    // ─── the extension ────────────────────────────────────────────────────

    /// <summary>The URL wins, because it reflects the actual file. A mirror serving
    /// an EPUB as octet-stream is common; a URL ending in .epub is not a guess.</summary>
    [Fact]
    public void The_extension_comes_from_the_url_when_it_has_one()
    {
        NameFor("Dune", url: "https://mirror.test/get/whatever.pdf", contentType: "application/epub+zip")
            .Should().Be("Dune.pdf");
    }

    /// <summary>Most mirror links end in an opaque id, so the content type is the
    /// fallback — and it has to cover the formats this library actually holds.</summary>
    [Theory]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/epub+zip", ".epub")]
    [InlineData("application/x-mobipocket-ebook", ".mobi")]
    [InlineData("application/vnd.amazon.ebook", ".azw3")]
    public void Without_one_in_the_url_the_content_type_decides(string contentType, string expected)
    {
        NameFor("Dune", url: "https://mirror.test/download/9f3c2a", contentType: contentType)
            .Should().Be("Dune" + expected);
    }

    /// <summary>
    /// An unrecognised type saves as <c>.bin</c> rather than guessing. Worth knowing
    /// what that means downstream: the library index only picks up known ebook
    /// extensions, so a <c>.bin</c> lands on disk and never appears on the shelf —
    /// visible as a send that reported success and produced no book.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    [InlineData(null)]
    public void An_unrecognised_type_saves_as_bin_rather_than_guessing(string? contentType)
    {
        NameFor("Dune", url: "https://mirror.test/download/9f3c2a", contentType: contentType)
            .Should().Be("Dune.bin");
    }

    /// <summary>
    /// <b>The extension is not sanitised the way the title is</b> — it is taken
    /// straight off the URL. This pins how far that can go: percent-encoding in a
    /// URL's path is not decoded by <c>AbsolutePath</c>, so a separator smuggled in
    /// as <c>%2F</c> stays literal text and cannot open a directory. Recorded because
    /// the asymmetry is real and someone will notice it later.
    /// </summary>
    [Theory]
    [InlineData("https://mirror.test/get/x.ep%2Fub")]
    [InlineData("https://mirror.test/get/x.e%00pub")]
    public void An_extension_smuggled_through_the_url_cannot_become_a_separator(string url)
    {
        var name = NameFor("Dune", url: url, contentType: null);

        name.Should().NotContain("/").And.NotContain("\\");
        Path.GetFileName(name).Should().Be(name);
    }

    /// <summary>A URL whose last segment has no dot leaves the content type to
    /// decide, rather than producing a name with no extension at all.</summary>
    [Fact]
    public void A_url_with_a_dot_in_an_earlier_segment_does_not_supply_the_extension()
    {
        NameFor("Dune", url: "https://mirror.test/v1.2/download/9f3c2a", contentType: "application/pdf")
            .Should().Be("Dune.pdf");
    }

    // ─── the parts agree ──────────────────────────────────────────────────

    /// <summary>The three returned values are one answer, not three. A caller writing
    /// <c>SafeTitle + Ext</c> itself must get the same name that was written to disk.</summary>
    [Fact]
    public void The_returned_parts_compose_into_the_returned_name()
    {
        var (safeTitle, ext, fileName) =
            BookFileNaming.For("Dune: Part Two", Md5, "https://mirror.test/get/x.epub", Served(null));

        (safeTitle + ext).Should().Be(fileName);
        fileName.Should().Be("Dune_ Part Two.epub");
    }
}
