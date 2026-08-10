namespace AnnasArchive.API.Reader2.Epub;

/// <summary>A chapter's structure paired with the text it was measured from.</summary>
public sealed record ExtractedChapter(Chapter Chapter, string Text);

/// <summary>
/// Merges the spine (what to read, in order) with the table of contents (what
/// each part is called) into the index a reader navigates by.
///
/// <para>The spine is authoritative for <i>order and existence</i>, the TOC only
/// for <i>titles and depth</i>. Doing it the other way round is the classic
/// mistake: a TOC routinely omits front matter, points several entries at one
/// file, or is missing altogether, and a TOC-driven index silently loses whole
/// chapters when it does.</para>
/// </summary>
public static class ChapterIndexBuilder
{
    /// <summary>
    /// Reads and measures every spine item. Text comes back alongside the index
    /// so the caller can write it without re-parsing the archive.
    /// </summary>
    public static (ChapterIndex Index, IReadOnlyList<ExtractedChapter> Chapters) Build(EpubPackage package)
    {
        var titlesBySource = TitlesBySource(package);
        var chapters = new List<ExtractedChapter>();

        for (var id = 0; id < package.Spine.Count; id++)
        {
            var item = package.Spine[id];
            var source = package.PathOf(item);
            var text = EpubTextExtractor.ToPlainText(package.ReadText(item) ?? "");

            var titled = titlesBySource.GetValueOrDefault(source);

            chapters.Add(new ExtractedChapter(
                new Chapter(
                    id,
                    titled?.Title ?? EpubTextExtractor.FirstLineTitle(text) ?? $"Chapter {id + 1}",
                    titled?.Level ?? 0,
                    EpubTextExtractor.CountWords(text),
                    source),
                text));
        }

        var index = new ChapterIndex(
            package.Title ?? "Untitled",
            chapters.Select(c => c.Chapter).ToList());

        return (index, chapters);
    }

    /// <summary>
    /// TOC titles keyed by the file they point at.
    ///
    /// <para>Several TOC entries can target one file — a chapter with named
    /// sub-sections, typically. The first wins, because it is the outermost and
    /// therefore names the file as a whole; later ones name parts of it.</para>
    /// </summary>
    private static Dictionary<string, TocEntry> TitlesBySource(EpubPackage package)
    {
        var titles = new Dictionary<string, TocEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in EpubNavigation.Read(package))
            titles.TryAdd(entry.Target, entry);

        return titles;
    }
}
