using System.Collections.Concurrent;

namespace AnnasArchive.API.Reader2.Domain;

/// <summary>
/// Remembers each EPUB's content hash so identity resolution stays cheap.
///
/// <para>Hashing is only unavoidable twice: when a book is first enrolled, and
/// when a file has actually changed. Everything else — every open, every
/// re-location scan — should be a dictionary lookup, which is what makes
/// resolution O(1) instead of Reader I's scan of every sidecar per request.</para>
///
/// <para>Keyed on file name <i>plus size plus last-write time</i>, so an edited
/// file re-hashes and an untouched one never does. Size alone would miss an
/// in-place edit of identical length; mtime alone would re-hash after a
/// no-op touch.</para>
/// </summary>
public sealed class ContentHashCache(ILibraryBookSource library)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(long Length, DateTime LastWriteUtc, BookRef Book);

    /// <summary>
    /// The book id for a library file, or null if the file is gone.
    /// Hashes only on a miss or after the file changed.
    /// </summary>
    public async Task<BookRef?> GetAsync(string fileName, CancellationToken ct = default)
    {
        var stat = library.Stat(fileName);
        if (stat is null) return null;
        var (length, lastWrite) = stat.Value;

        if (_entries.TryGetValue(fileName, out var cached) &&
            cached.Length == length && cached.LastWriteUtc == lastWrite)
        {
            return cached.Book;
        }

        await using var stream = library.OpenRead(fileName);
        if (stream is null) return null;

        var book = await BookRef.FromStreamAsync(stream, ct);
        _entries[fileName] = new Entry(length, lastWrite, book);
        return book;
    }

    /// <summary>
    /// Finds the library file whose contents match <paramref name="book"/>, or
    /// null if none does. This is how a renamed or moved book is recovered
    /// rather than orphaned.
    /// </summary>
    public async Task<string?> FindFileAsync(BookRef book, CancellationToken ct = default)
    {
        foreach (var fileName in library.EnumerateEpubFileNames())
        {
            ct.ThrowIfCancellationRequested();
            if (await GetAsync(fileName, ct) == book) return fileName;
        }

        return null;
    }

    /// <summary>Drops a remembered hash — for tests, and for a file known to have gone.</summary>
    public void Forget(string fileName) => _entries.TryRemove(fileName, out _);

    /// <summary>Remembered files. For tests and diagnostics.</summary>
    public int Count => _entries.Count;
}
