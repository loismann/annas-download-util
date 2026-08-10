using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;
using Serilog;

namespace AnnasArchive.API.Reader2.Epub;

/// <summary>One step of a long operation, for the SSE stream.</summary>
public sealed record ProgressStep(string Stage, int Current, int Total, string Message);

/// <summary>
/// Turns an enrolled book into something readable: extracts every chapter to
/// text and records the index.
///
/// <para><b>Order is the whole design.</b> Chapter files are written first and
/// the index artifact last, so the index is the commit point — it exists only
/// once every chapter it describes is on disk. A crash halfway leaves loose
/// text files and no index, which is a re-runnable state rather than a book
/// that claims twelve chapters and can open nine.</para>
/// </summary>
public sealed class BookIngestor(
    ILibraryBookSource library,
    ChapterTextStore text,
    IArtifactStore artifacts)
{
    /// <summary>
    /// Extracts a book, reporting progress. Safe to call repeatedly: an already
    /// complete ingest returns its existing index without touching the archive,
    /// and anything less than complete is extracted again from the start.
    ///
    /// <para>Redoing a partial ingest rather than filling in its gaps is the
    /// cheaper correct answer — extraction is seconds of local work, whereas
    /// deciding which of the existing files are trustworthy is not something the
    /// filesystem can tell us.</para>
    /// </summary>
    public async Task<ChapterIndex> IngestAsync(
        EnrolledBook book,
        IProgress<ProgressStep>? progress = null,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!force && await CompleteIndexAsync(book.Book, ct) is { } done)
        {
            progress?.Report(new ProgressStep("complete", 1, 1, "Already indexed."));
            return done;
        }

        progress?.Report(new ProgressStep("opening", 0, 1, $"Opening {book.Title}…"));

        await using var stream = library.OpenRead(book.FileName)
            ?? throw new EpubException($"'{book.FileName}' is no longer in the library.");

        using var package = EpubPackage.Open(stream);
        var (index, chapters) = ChapterIndexBuilder.Build(package);

        for (var i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chapter = chapters[i];
            progress?.Report(new ProgressStep(
                "extracting", i + 1, chapters.Count,
                $"Extracting chapter {i + 1} of {chapters.Count}…"));

            await text.WriteChapterAsync(book.Book, chapter.Chapter.Id, chapter.Text, ct);
        }

        // Last, deliberately: writing this is what declares the book readable.
        await artifacts.PutAsync(
            ArtifactKey.ChapterIndex(book.Book), index,
            ArtifactProvenance.Computed(ChapterIndex.CurrentSchemaVersion), ct);

        Log.Information(
            "[reader2] Indexed {Title}: {Chapters} chapters, {Words} words",
            index.Title, index.Chapters.Count, index.TotalWords);

        progress?.Report(new ProgressStep("complete", chapters.Count, chapters.Count,
            $"Indexed {index.Chapters.Count} chapters."));

        return index;
    }

    /// <summary>
    /// The stored index, but only if every chapter it names is still on disk.
    ///
    /// <para>The pair can come apart — a half-finished delete, a pruned cache
    /// directory — and an index whose text is missing is worse than no index,
    /// because the reader opens a chapter and finds nothing. Treating that as
    /// "not ingested" makes the repair automatic.</para>
    /// </summary>
    public async Task<ChapterIndex?> CompleteIndexAsync(BookRef book, CancellationToken ct = default)
    {
        var stored = await artifacts.GetAsync<ChapterIndex>(
            ArtifactKey.ChapterIndex(book),
            ArtifactVersions.Computed(ChapterIndex.CurrentSchemaVersion), ct);

        if (stored is null) return null;

        var extracted = text.ExtractedChapters(book).ToHashSet();
        var missing = stored.Content.Chapters.Count(c => !extracted.Contains(c.Id));

        if (missing == 0) return stored.Content;

        Log.Information(
            "[reader2] {Book} has an index but {Missing} of {Total} chapters are missing; re-extracting",
            book, missing, stored.Content.Chapters.Count);

        return null;
    }
}
