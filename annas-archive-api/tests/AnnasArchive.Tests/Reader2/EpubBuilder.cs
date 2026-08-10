using System.IO.Compression;
using System.Text;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Builds EPUB fixtures in memory.
///
/// Generated rather than checked in: a binary fixture is unreviewable, and the
/// interesting thing about each of these is *how it is malformed*, which only
/// shows up as code. Adding a case is a method here, not a file nobody can diff.
/// </summary>
public sealed class EpubBuilder
{
    private readonly List<(string Path, string Content)> _files = [];
    private readonly List<(string Id, string Href, string Title, int Level)> _chapters = [];
    private string _title = "Test Book";
    private string _author = "Test Author";
    private Toc _toc = Toc.Epub3Nav;
    private bool _validContainer = true;
    private bool _declareMediaTypes = true;
    private bool _escapeHrefs;
    private string _mimetype = "application/epub+zip";
    private string _opfDirectory = "OEBPS";

    public enum Toc { Epub3Nav, Epub2Ncx, None }

    public EpubBuilder Titled(string title, string author = "Test Author")
    {
        _title = title;
        _author = author;
        return this;
    }

    public EpubBuilder WithToc(Toc toc) { _toc = toc; return this; }
    public EpubBuilder WithBrokenContainer() { _validContainer = false; return this; }
    public EpubBuilder WithMimetype(string mimetype) { _mimetype = mimetype; return this; }
    public EpubBuilder InDirectory(string directory) { _opfDirectory = directory; return this; }

    /// <summary>Manifest items with no <c>media-type</c> at all.</summary>
    public EpubBuilder WithoutMediaTypes() { _declareMediaTypes = false; return this; }

    /// <summary>Percent-encoded manifest hrefs pointing at literally-named entries.</summary>
    public EpubBuilder WithEscapedHrefs() { _escapeHrefs = true; return this; }

    /// <param name="level">TOC nesting depth, for the nested-TOC fixture.</param>
    public EpubBuilder Chapter(string title, string body, int level = 0, string? href = null)
    {
        var id = $"ch{_chapters.Count + 1}";
        var file = href ?? $"{id}.xhtml";

        _chapters.Add((id, file, title, level));
        // A style block on every chapter, so "markup is stripped" is exercised by
        // the fixtures rather than only by a unit test. Held in a const because
        // its braces would otherwise read as interpolation holes.
        const string style = "<style>.x { color: red; }</style>";

        _files.Add(($"{_opfDirectory}/{file}", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{title}</title>
            {style}</head>
            <body><h1>{title}</h1>{body}</body></html>
            """));
        return this;
    }

    /// <summary>Several paragraphs, for chunking and search tests.</summary>
    public EpubBuilder ChapterOfParagraphs(string title, int paragraphs, int wordsEach, string word = "word")
    {
        var body = string.Join("\n", Enumerable.Range(0, paragraphs)
            .Select(p => $"<p>{string.Join(' ', Enumerable.Repeat($"{word}{p}", wordsEach))}</p>"));
        return Chapter(title, body);
    }

    public byte[] Build()
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", _mimetype);

            if (_validContainer)
                Add(zip, "META-INF/container.xml", $"""
                    <?xml version="1.0"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="{_opfDirectory}/content.opf"
                        media-type="application/oebps-package+xml"/></rootfiles>
                    </container>
                    """);

            Add(zip, $"{_opfDirectory}/content.opf", Opf());

            switch (_toc)
            {
                case Toc.Epub3Nav: Add(zip, $"{_opfDirectory}/nav.xhtml", NavDocument()); break;
                case Toc.Epub2Ncx: Add(zip, $"{_opfDirectory}/toc.ncx", Ncx()); break;
            }

            foreach (var (path, content) in _files) Add(zip, path, content);
        }

        return buffer.ToArray();
    }

    private string Opf()
    {
        var manifest = new StringBuilder();
        var mediaType = _declareMediaTypes ? " media-type=\"application/xhtml+xml\"" : "";

        foreach (var (id, href, _, _) in _chapters)
            manifest.AppendLine(
                $"""    <item id="{id}" href="{(_escapeHrefs ? Uri.EscapeDataString(href) : href)}"{mediaType}/>""");

        if (_toc == Toc.Epub3Nav)
            manifest.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
        if (_toc == Toc.Epub2Ncx)
            manifest.AppendLine("""    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""");

        var spine = string.Join("\n", _chapters.Select(c => $"""    <itemref idref="{c.Id}"/>"""));
        var tocAttr = _toc == Toc.Epub2Ncx ? " toc=\"ncx\"" : "";

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{_title}</dc:title>
                <dc:creator>{_author}</dc:creator>
              </metadata>
              <manifest>
            {manifest}  </manifest>
              <spine{tocAttr}>
            {spine}
              </spine>
            </package>
            """;
    }

    private string NavDocument()
    {
        var body = new StringBuilder();
        var depth = 0;

        foreach (var (_, href, title, level) in _chapters)
        {
            while (depth < level) { body.AppendLine("<ol>"); depth++; }
            while (depth > level) { body.AppendLine("</ol>"); depth--; }
            body.AppendLine($"""<li><a href="{href}">{title}</a></li>""");
        }
        while (depth-- > 0) body.AppendLine("</ol>");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <body><nav epub:type="toc"><ol>
            {body}
            </ol></nav></body></html>
            """;
    }

    private string Ncx()
    {
        var points = new StringBuilder();
        var open = 0;

        foreach (var (id, href, title, level) in _chapters)
        {
            while (open > level) { points.AppendLine("</navPoint>"); open--; }
            points.AppendLine($"""
                <navPoint id="np-{id}"><navLabel><text>{title}</text></navLabel>
                <content src="{href}"/>
                """);
            open = level + 1;
        }
        while (open-- > 0) points.AppendLine("</navPoint>");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
            <navMap>
            {points}
            </navMap></ncx>
            """;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // ─── the fixture set ─────────────────────────────────────────────────

    public static byte[] Epub3WithNav() => new EpubBuilder()
        .Titled("An EPUB 3 Book")
        .Chapter("The Beginning", "<p>It was a bright cold day in April.</p>")
        .Chapter("The Middle", "<p>The clocks were striking thirteen.</p>")
        .Chapter("The End", "<p>He loved Big Brother.</p>")
        .Build();

    public static byte[] Epub2WithNcxOnly() => new EpubBuilder()
        .Titled("An EPUB 2 Book")
        .WithToc(Toc.Epub2Ncx)
        .Chapter("First Part", "<p>Call me Ishmael.</p>")
        .Chapter("Second Part", "<p>There now is your insular city.</p>")
        .Build();

    public static byte[] NoToc() => new EpubBuilder()
        .Titled("A Book With No Contents")
        .WithToc(Toc.None)
        .Chapter("Ignored", "<p>The spine alone must carry this.</p>")
        .Chapter("Also Ignored", "<p>And this second one too.</p>")
        .Build();

    public static byte[] NestedTocDepthThree() => new EpubBuilder()
        .Titled("A Nested Book")
        .Chapter("Part One", "<p>Top level.</p>")
        .Chapter("Chapter A", "<p>One deep.</p>", level: 1)
        .Chapter("Section i", "<p>Two deep.</p>", level: 2)
        .Chapter("Part Two", "<p>Back to the top.</p>")
        .Build();

    public static byte[] NonAsciiFileNames() => new EpubBuilder()
        .Titled("Война и миръ")
        .Chapter("Глава первая", "<p>Ну, князь, Генуа и Лукка.</p>", href: "глава-1.xhtml")
        .Chapter("Chapitre deuxième", "<p>Où il est question de Naïveté.</p>", href: "chapitre-deuxième.xhtml")
        .Build();

    /// <summary>A wrong mimetype is common and every e-reader ignores it.</summary>
    public static byte[] MisdeclaredMimetype() => new EpubBuilder()
        .Titled("A Sloppy Book")
        .WithMimetype("text/plain")
        .Chapter("Still Readable", "<p>The mimetype lies, the content does not.</p>")
        .Build();

    /// <summary>No container.xml — the package must still be found.</summary>
    public static byte[] MissingContainer() => new EpubBuilder()
        .Titled("A Book With No Container")
        .WithBrokenContainer()
        .Chapter("Found Anyway", "<p>Located by looking for the OPF directly.</p>")
        .Build();

    /// <summary>
    /// No media-type on any manifest item. Without an extension fallback the
    /// spine empties and the book reports itself as having no chapters.
    /// </summary>
    public static byte[] MissingMediaTypes() => new EpubBuilder()
        .Titled("A Book With Undeclared Types")
        .WithoutMediaTypes()
        .Chapter("Still A Chapter", "<p>Recognised by its extension.</p>")
        .Chapter("So Is This", "<p>And so is this one.</p>")
        .Build();

    /// <summary>Hrefs escaped in the OPF but literal in the archive.</summary>
    public static byte[] PercentEncodedHrefs() => new EpubBuilder()
        .Titled("A Book With Escaped Hrefs")
        .WithEscapedHrefs()
        .Chapter("Spaces And All", "<p>Found despite the encoding.</p>", href: "chapter one.xhtml")
        .Build();

    public static byte[] Corrupt() => "this is not a zip file, it is a sentence"u8.ToArray();
}
