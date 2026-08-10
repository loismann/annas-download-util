namespace AnnasArchive.API.Reader2.Epub;

/// <summary>One table-of-contents entry, flattened with its nesting depth.</summary>
/// <param name="Target">In-archive path of the file it points at, fragment removed.</param>
/// <param name="Level">0 for a top-level entry, deeper for nested ones.</param>
public sealed record TocEntry(string Title, string Target, int Level);

/// <summary>
/// Reads a book's table of contents from whichever dialect it uses.
///
/// <para>EPUB 3 publishes an XHTML <c>nav</c> document; EPUB 2 uses an NCX. Both
/// are still common — a 2011 book and a 2024 book are both things a reader
/// opens — so both are read, and a book with neither still works because the
/// spine alone gives reading order.</para>
/// </summary>
public static class EpubNavigation
{
    /// <summary>
    /// The TOC, or an empty list if the book has none or it is unreadable.
    /// A missing TOC is not an error: <see cref="ChapterIndexBuilder"/> falls
    /// back to the spine, which is always present.
    /// </summary>
    public static IReadOnlyList<TocEntry> Read(EpubPackage package)
    {
        if (package.NavigationDocument is { } nav && package.ReadText(nav) is { } navXml)
        {
            var entries = ReadNavDocument(navXml, ZipPath.DirectoryOf(package.PathOf(nav)));
            if (entries.Count > 0) return entries;
        }

        if (package.Ncx is { } ncx && package.ReadText(ncx) is { } ncxXml)
            return ReadNcx(ncxXml, ZipPath.DirectoryOf(package.PathOf(ncx)));

        return [];
    }

    /// <summary>
    /// EPUB 3: <c>&lt;nav epub:type="toc"&gt;</c> wrapping nested <c>&lt;ol&gt;</c>s.
    /// Depth comes from how many lists an entry sits inside.
    /// </summary>
    private static List<TocEntry> ReadNavDocument(string xml, string baseDirectory)
    {
        if (EpubXml.TryParse(xml) is not { } doc) return [];

        // Prefer the nav explicitly typed as the TOC; a book may also carry a
        // landmarks or page-list nav, and those are not chapters.
        var navs = doc.Named("nav").ToList();
        var toc = navs.FirstOrDefault(n => n.Attributes()
                      .Any(a => a.Name.LocalName == "type" && a.Value.Contains("toc")))
                  ?? navs.FirstOrDefault();

        if (toc is null) return [];

        return toc.Named("a")
            .Select(a => new TocEntry(
                Clean(a.Value),
                ZipPath.Combine(baseDirectory, EpubXml.Attr(a, "href")),
                Math.Max(0, a.Ancestors().Count(x => x.Name.LocalName == "ol") - 1)))
            .Where(Usable)
            .ToList();
    }

    /// <summary>
    /// EPUB 2: <c>&lt;navMap&gt;</c> of <c>&lt;navPoint&gt;</c>s, which nest
    /// directly rather than through lists.
    /// </summary>
    private static List<TocEntry> ReadNcx(string xml, string baseDirectory)
    {
        if (EpubXml.TryParse(xml) is not { } doc) return [];

        return doc.Named("navPoint")
            .Select(point => new TocEntry(
                Clean(point.FirstNamed("text")?.Value ?? ""),
                ZipPath.Combine(baseDirectory, EpubXml.Attr(point.FirstNamed("content"), "src")),
                point.Ancestors().Count(a => a.Name.LocalName == "navPoint")))
            .Where(Usable)
            .ToList();
    }

    /// <summary>An entry with no title or no destination cannot be navigated to.</summary>
    private static bool Usable(TocEntry entry) => entry.Title.Length > 0 && entry.Target.Length > 0;

    private static string Clean(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
