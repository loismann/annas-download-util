using AnnasArchive.API.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Whether there was a book here to delete.
///
/// <para>This used to report a count per category — book file, sidecar, covers,
/// and for a while two cache counters as well. Nothing ever read them: one call
/// site checks <see cref="Found"/>, the other discards the result, and the
/// endpoint answers <c>{ success = true }</c>, so the counts never crossed the
/// wire in the whole life of the record. Detail nobody consumes still has to be
/// kept correct by everyone who edits the method, which is a cost with no
/// reader; the per-file outcomes are in the log, where a person looking into a
/// failed delete actually goes.</para>
/// </summary>
public record LibraryDeletionResult(bool Found);

/// <summary>
/// Deletes a library book and every trace of it across the system: the ebook file itself, its
/// metadata sidecar and cover image(s). Shared by the general
/// single-book delete endpoint and the library-review modal's "delete" decision, so both paths
/// get the same permanent, complete removal.
/// </summary>
public static class LibraryBookDeletionHelper
{
    public static LibraryDeletionResult DeleteBookCompletely(
        string fileName,
        LibraryIndexCache cache,
        Data.BookPersonalizationStore? personalization = null)
    {
        var safeFileName = Path.GetFileName(fileName);

        // User personalization row goes too — a future book with the same file name
        // must not inherit a deleted book's favorites/tags.
        personalization?.Delete(safeFileName);

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var bookPath = Path.Combine(libraryRoot, safeFileName);
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");
        var coverDir = Path.Combine(libraryRoot, "_covers");
        var coverMatches = Directory.Exists(coverDir)
            ? Directory.GetFiles(coverDir, $"{safeFileName}.cover.*")
            : Array.Empty<string>();

        var bookFileExists = File.Exists(bookPath);
        var metaFileExists = File.Exists(metaPath);

        if (!bookFileExists && !metaFileExists && coverMatches.Length == 0)
            return new LibraryDeletionResult(false);

        if (bookFileExists)
            File.Delete(bookPath);

        if (metaFileExists)
            File.Delete(metaPath);

        var coverFilesDeleted = 0;
        foreach (var cover in coverMatches)
        {
            try
            {
                File.Delete(cover);
                coverFilesDeleted++;
            }
            catch (Exception ex)
            {
                // Logged rather than reported: a cover that will not delete leaves a
                // stray file, not a book the reader can still see.
                Log.Warning(ex, "[LibraryBookDeletion] Failed to delete cover {Cover}", cover);
            }
        }

        Log.Information(
            "[LibraryBookDeletion] {FileName}: book={Book} sidecar={Sidecar} covers={Covers}/{CoverTotal}",
            safeFileName, bookFileExists, metaFileExists, coverFilesDeleted, coverMatches.Length);

        // The reader keeps nothing keyed to this path: its text is content-addressed
        // and its artifacts are keyed on the content hash, so a book whose file is gone
        // is marked unavailable and keeps everything the reader paid for. That is the
        // reader's own design decision, not an omission here.
        cache.RemoveBook(safeFileName);

        return new LibraryDeletionResult(true);
    }
}
