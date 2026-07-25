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
    int CoverFilesDeleted,
    bool AiCacheDeleted,
    bool EpubCacheDeleted);

/// <summary>
/// Deletes a library book and every trace of it across the system: the ebook file itself, its
/// metadata sidecar, cover image(s), and its per-book AI/epub caches. Shared by the general
/// single-book delete endpoint and the library-review modal's "delete" decision, so both paths
/// get the same permanent, complete removal.
/// </summary>
public static class LibraryBookDeletionHelper
{
    public static LibraryDeletionResult DeleteBookCompletely(string fileName, LibraryIndexCache cache)
    {
        var safeFileName = Path.GetFileName(fileName);

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
            return new LibraryDeletionResult(false, false, false, 0, false, false);

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
                Log.Warning("[LibraryBookDeletion] Failed to delete cover {Cover}: {Message}", cover, ex.Message);
            }
        }

        // Purge per-book AI summary caches (chapter/ultra/section summaries, chunk boundaries,
        // character graph) and the epub chapter-index cache — neither is scoped to the plain
        // ebook file, so they'd otherwise survive a "delete" untouched.
        var existingKeys = AiContentCache.GetExistingSummaryKeys();
        var readerKey = AiSummaryHelpers.ResolveReaderKey(safeFileName, existingKeys);
        var aiCacheDeleted = AiContentCache.DeleteAllAiCacheForBook(readerKey);
        var epubCacheDeleted = LibraryEpubCache.DeleteCache(readerKey);

        cache.RemoveBook(safeFileName);

        return new LibraryDeletionResult(true, bookFileDeleted, metaFileDeleted, coverFilesDeleted, aiCacheDeleted, epubCacheDeleted);
    }
}
