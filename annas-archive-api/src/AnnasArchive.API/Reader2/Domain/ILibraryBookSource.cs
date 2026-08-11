using AnnasArchive.API.Helpers;

namespace AnnasArchive.API.Reader2.Domain;

/// <summary>
/// Reader II's one seam onto the library's EPUB files.
///
/// <para>The library is an application feature, not a reader feature, so Reader II
/// reads the same files everything else does rather than keeping its own copy.
/// Routing that access through one interface means the library root is named in
/// exactly one implementation — Reader I resolved it in seven places, and scanned
/// every <c>.meta.json</c> on every request to turn a key back into a path.</para>
/// </summary>
public interface ILibraryBookSource
{
    /// <summary>Every EPUB in the library, as bare file names.</summary>
    IReadOnlyList<string> EnumerateEpubFileNames();

    bool Exists(string fileName);

    /// <summary>Opens an EPUB for reading, or null if it is gone.</summary>
    Stream? OpenRead(string fileName);

    /// <summary>Size and last-write time, or null if the file is gone. Used to
    /// decide whether a cached content hash is still valid.</summary>
    (long Length, DateTime LastWriteUtc)? Stat(string fileName);

    /// <summary>
    /// The library's cover image for a book, absolute, or null if it has none.
    ///
    /// <para>Here rather than resolved by the reader, because a cover is not one
    /// rule: it is a URL in the book's <c>.meta.json</c>, or an external address
    /// left there by a search, or a file in <c>_covers</c> whose extension is
    /// whatever was downloaded. The library already answers all three, and a
    /// second implementation would be a second set of books with no picture.</para>
    /// </summary>
    string? CoverUrl(string fileName, string baseUrl);
}

/// <inheritdoc />
public sealed class LibraryBookSource(Services.LibraryIndexCache covers) : ILibraryBookSource
{
    private static string Root => LibraryHelpers.ResolveLibraryRoot();

    /// <inheritdoc />
    /// <remarks>
    /// Straight off the library's own index — the same list, the same resolution,
    /// the same answer the library page shows. It is a warmed cache, so this costs
    /// a scan of a list already in memory rather than a walk of the disk.
    /// </remarks>
    public string? CoverUrl(string fileName, string baseUrl)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe)) return null;

        return covers.GetBooks(baseUrl)
            .FirstOrDefault(b => string.Equals(b.FileName, safe, StringComparison.OrdinalIgnoreCase))
            ?.CoverUrl;
    }

    public IReadOnlyList<string> EnumerateEpubFileNames()
    {
        var root = Root;
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "*.epub", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();
    }

    public bool Exists(string fileName) => Resolve(fileName) is { } p && File.Exists(p);

    public Stream? OpenRead(string fileName)
    {
        var path = Resolve(fileName);
        return path is not null && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    public (long Length, DateTime LastWriteUtc)? Stat(string fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return null;

        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>
    /// Confines every name to the library directory. A stored file name comes
    /// from our own table, but it reaches here from route values too, and one
    /// path-traversal slip would let the reader open arbitrary files.
    /// </summary>
    private static string? Resolve(string fileName)
    {
        var safe = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(safe) ? null : Path.Combine(Root, safe);
    }
}
