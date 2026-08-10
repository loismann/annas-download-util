using System.IO.Compression;

namespace AnnasArchive.API.Reader2.Epub;

/// <summary>A failure a reader can be shown, rather than a stack trace.</summary>
public sealed class EpubException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>One file listed in the OPF manifest.</summary>
public sealed record ManifestItem(string Id, string Href, string MediaType, string Properties)
{
    /// <summary>EPUB 3 marks its table of contents with <c>properties="nav"</c>.</summary>
    public bool IsNavigation => Properties.Split(' ').Contains("nav");

    /// <summary>
    /// Prose the reader should see.
    ///
    /// <para>The href extension is a fallback for items with no declared
    /// media-type, which happens in hand-assembled books. Without it those items
    /// fall out of the spine, and a book whose spine empties is reported as
    /// having no chapters at all — a severe failure for a cosmetic defect.</para>
    /// </summary>
    public bool IsContent =>
        MediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
        (MediaType.Length == 0 && HasHtmlExtension);

    private bool HasHtmlExtension =>
        Href.Split('#')[0] is var path &&
        (path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// An open EPUB: its spine (reading order), its navigation, and its metadata.
///
/// <para>Lenient by design — see <see cref="EpubXml"/> for why every lookup
/// ignores namespaces. The same reasoning runs through the rest of this class:
/// a missing container, a percent-encoded href, or a wrong mimetype are all
/// recovered from rather than rejected.</para>
/// </summary>
public sealed class EpubPackage : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> _entries;
    private readonly string _opfDirectory;

    /// <summary>Manifest items in reading order — the spine, resolved.</summary>
    public IReadOnlyList<ManifestItem> Spine { get; }

    public string? Title { get; }
    public IReadOnlyList<string> Authors { get; }

    /// <summary>The EPUB 3 navigation document, if this book has one.</summary>
    public ManifestItem? NavigationDocument { get; }

    /// <summary>The EPUB 2 NCX, named by the spine's <c>toc</c> attribute.</summary>
    public ManifestItem? Ncx { get; }

    private EpubPackage(
        ZipArchive zip, IReadOnlyDictionary<string, ZipArchiveEntry> entries, string opfDirectory,
        IReadOnlyList<ManifestItem> spine, string? title, IReadOnlyList<string> authors,
        ManifestItem? nav, ManifestItem? ncx)
    {
        _zip = zip;
        _entries = entries;
        _opfDirectory = opfDirectory;
        Spine = spine;
        Title = title;
        Authors = authors;
        NavigationDocument = nav;
        Ncx = ncx;
    }

    public static EpubPackage Open(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new EpubException("This file is not a readable EPUB archive.", ex);
        }

        try
        {
            return Parse(zip);
        }
        catch (EpubException)
        {
            zip.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            zip.Dispose();
            throw new EpubException("This EPUB's structure could not be read.", ex);
        }
    }

    private static EpubPackage Parse(ZipArchive zip)
    {
        var entries = IndexEntries(zip);
        var opfPath = FindOpfPath(zip, entries);

        var opf = Read(entries, opfPath) is { } xml && EpubXml.TryParse(xml) is { } parsed
            ? parsed
            : throw new EpubException("This EPUB's package file is missing or unreadable.");

        var manifest = opf.Named("item")
            .Where(e => e.Parent?.Name.LocalName == "manifest")
            .Select(e => new ManifestItem(
                EpubXml.Attr(e, "id"), EpubXml.Attr(e, "href"),
                EpubXml.Attr(e, "media-type"), EpubXml.Attr(e, "properties")))
            .Where(i => i.Id.Length > 0 && i.Href.Length > 0)
            .ToList();

        var byId = manifest
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var spineElement = opf.FirstNamed("spine");

        // linear="no" marks matter a reader can skip — cover pages, adverts. They
        // stay out of the reading order, which is what the spine is for.
        var spine = (spineElement?.Named("itemref") ?? [])
            .Where(e => !string.Equals(EpubXml.Attr(e, "linear"), "no", StringComparison.OrdinalIgnoreCase))
            .Select(e => byId.GetValueOrDefault(EpubXml.Attr(e, "idref")))
            .Where(i => i is not null && i.IsContent)
            .Select(i => i!)
            .ToList();

        if (spine.Count == 0)
            throw new EpubException("This EPUB contains no readable chapters.");

        var ncxId = EpubXml.Attr(spineElement, "toc");

        return new EpubPackage(
            zip,
            entries,
            ZipPath.DirectoryOf(opfPath),
            spine,
            opf.FirstNamed("title")?.Value.Trim(),
            opf.Named("creator").Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList(),
            manifest.FirstOrDefault(i => i.IsNavigation),
            ncxId.Length > 0 ? byId.GetValueOrDefault(ncxId) : null);
    }

    /// <summary>
    /// Reads a manifest item's text, resolving its href against the OPF's own
    /// directory — hrefs are relative to the package file, not the archive root.
    /// </summary>
    public string? ReadText(ManifestItem item) => Read(_entries, PathOf(item));

    /// <summary>Absolute in-archive path of a manifest item, for TOC matching.</summary>
    public string PathOf(ManifestItem item) => ZipPath.Combine(_opfDirectory, item.Href);

    /// <summary>
    /// Every entry, keyed the one way a lookup is allowed to ask for it.
    ///
    /// <para>Built once. Percent-encoded hrefs ("chapter%201.xhtml") and case
    /// differences are both common in real books, and matching them by scanning
    /// the archive per lookup is quadratic in a book with hundreds of chapters.
    /// Folding both into the key makes every lookup a single hash.</para>
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries) entries.TryAdd(EntryKey(entry.FullName), entry);

        return entries;
    }

    private static string EntryKey(string path) => ZipPath.Normalize(Uri.UnescapeDataString(path));

    private static string? Read(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(EntryKey(path), out var entry)) return null;

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string FindOpfPath(ZipArchive zip, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        var container = Read(entries, "META-INF/container.xml") is { } xml
            ? EpubXml.TryParse(xml)
            : null;

        var declared = EpubXml.Attr(container?.FirstNamed("rootfile"), "full-path");
        if (declared.Length > 0 && entries.ContainsKey(EntryKey(declared))) return declared;

        // No container, or it points at nothing. Rather than give up, look for the
        // package file directly — a missing container.xml is one of the more common
        // ways a hand-assembled EPUB is malformed, and it is recoverable. Scanning
        // the archive rather than the index keeps the choice deterministic.
        var fallback = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));

        return fallback?.FullName
            ?? throw new EpubException("This EPUB has no package file, so its contents cannot be listed.");
    }

    public void Dispose() => _zip.Dispose();
}
