using AnnasArchive.API.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Result of a full-wipe book deletion, reporting which categories of data were actually found and removed.
/// </summary>
public record LibraryDeletionResult(
    bool Found,
    bool BookFileDeleted,
    bool MetaFileDeleted,
    int CoverFilesDeleted);

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
            return new LibraryDeletionResult(false, false, false, 0);

        var bookFileDeleted = false;
        if (bookFileExists)
        {
            File.Delete(bookPath);
            bookFileDeleted = true;
        }

        var metaFileDeleted = false;
        if (metaFileExists)
        {
            File.Delete(metaPath);
            metaFileDeleted = true;
        }

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
                Log.Warning(ex, "[LibraryBookDeletion] Failed to delete cover {Cover}", cover);
            }
        }

        // The reader keeps nothing keyed to this path: its text is content-addressed
        // and its artifacts are keyed on the content hash, so a book whose file is gone
        // is marked unavailable and keeps everything the reader paid for. That is the
        // reader's own design decision, not an omission here.
        cache.RemoveBook(safeFileName);

        return new LibraryDeletionResult(true, bookFileDeleted, metaFileDeleted, coverFilesDeleted);
    }
}
