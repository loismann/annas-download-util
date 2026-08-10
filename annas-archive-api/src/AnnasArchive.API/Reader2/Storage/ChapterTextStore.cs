using AnnasArchive.API.Reader2.Domain;
using Serilog;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// Where extracted chapter text lives — on disk, not in SQLite.
///
/// <para>Generated artifacts go in the database because they are small,
/// queryable, and need transactions. Extracted text is none of those: it is
/// large, purely derived from the EPUB, and streamed a chapter at a time.</para>
///
/// <para>Content-addressed by the book id, so renaming a library file costs
/// nothing and two copies of one book share a single extraction.</para>
/// </summary>
public sealed class ChapterTextStore
{
    private readonly string _root;

    public ChapterTextStore(IConfiguration configuration)
    {
        // Config first, then environment, then a default — the same precedence
        // ModelSelectionService uses, so one rule covers the whole application.
        var configured = configuration.GetValue<string>("Reader2:TextRoot")
            ?? Environment.GetEnvironmentVariable("READER2_TEXT_ROOT");

        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "reader2-text")
            : configured;
    }

    /// <summary>The directory holding one book's extracted chapters.</summary>
    public string DirectoryFor(BookRef book) => Path.Combine(_root, book.Value);

    /// <summary>
    /// The file for one chapter. Zero-padded so a directory listing sorts in
    /// reading order rather than 1, 10, 11, 2.
    /// </summary>
    public string ChapterFile(BookRef book, int chapter) =>
        Path.Combine(DirectoryFor(book), $"chapter-{chapter:D4}.txt");

    public bool HasChapter(BookRef book, int chapter) => File.Exists(ChapterFile(book, chapter));

    public Task<string> ReadChapterAsync(BookRef book, int chapter, CancellationToken ct = default) =>
        File.ReadAllTextAsync(ChapterFile(book, chapter), ct);

    /// <summary>
    /// A chapter's text, or null if it is not extracted.
    ///
    /// <para>Synchronous on purpose: whole-book search reads every chapter in a
    /// tight loop with no model and no network, and awaiting each one buys
    /// nothing over a local file a few tens of kilobytes long.</para>
    /// </summary>
    public string? TryReadChapter(BookRef book, int chapter)
    {
        var path = ChapterFile(book, chapter);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public async Task WriteChapterAsync(
        BookRef book, int chapter, string text, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryFor(book));
        await File.WriteAllTextAsync(ChapterFile(book, chapter), text, ct);
    }

    /// <summary>Chapter numbers already extracted, ascending. Used to resume a
    /// half-finished ingest and to answer "is this book readable yet".</summary>
    public IReadOnlyList<int> ExtractedChapters(BookRef book)
    {
        var dir = DirectoryFor(book);
        if (!Directory.Exists(dir)) return [];

        return Directory.EnumerateFiles(dir, "chapter-*.txt")
            .Select(f => int.TryParse(
                Path.GetFileNameWithoutExtension(f)["chapter-".Length..], out var n) ? n : -1)
            .Where(n => n >= 0)
            .OrderBy(n => n)
            .ToArray();
    }

    /// <summary>
    /// Removes a book's extracted text. Safe precisely because identity is the
    /// content hash: an identical hash <i>is</i> the same book, so no other
    /// enrolment can still need these files.
    /// </summary>
    public bool Delete(BookRef book)
    {
        var dir = DirectoryFor(book);
        if (!Directory.Exists(dir)) return false;

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[reader2] Could not delete extracted text for {Book}", book);
            return false;
        }
    }
}
