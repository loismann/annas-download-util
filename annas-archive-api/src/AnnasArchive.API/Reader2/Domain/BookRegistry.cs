using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Storage;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AnnasArchive.API.Reader2.Domain;

/// <summary>A book enrolled in Reader II.</summary>
/// <param name="IsAvailable">
/// Whether its file is currently on disk. Computed per read, never stored — a
/// book that vanishes and comes back should not need repairing.
/// </param>
public sealed record EnrolledBook(
    BookRef Book,
    string FileName,
    string Title,
    IReadOnlyList<string> Authors,
    string LensKey,
    DateTime AddedAtUtc,
    DateTime? LastOpenedAtUtc,
    bool IsAvailable);

/// <summary>
/// The books Reader II knows about, and the one place that turns a
/// <see cref="BookRef"/> back into a file on disk.
/// </summary>
public interface IBookRegistry
{
    Task<EnrolledBook?> GetAsync(BookRef book, CancellationToken ct = default);
    Task<IReadOnlyList<EnrolledBook>> ListAsync(CancellationToken ct = default);
    Task<EnrolledBook> EnrolAsync(
        BookRef book, string fileName, string title, IReadOnlyList<string> authors,
        string lensKey, CancellationToken ct = default);
    Task<bool> SetLensAsync(BookRef book, string lensKey, CancellationToken ct = default);
    Task TouchOpenedAsync(BookRef book, CancellationToken ct = default);
    Task<bool> RemoveAsync(BookRef book, CancellationToken ct = default);
}

/// <inheritdoc cref="IBookRegistry" />
public sealed class BookRegistry(
    AppDatabase db,
    ILibraryBookSource library,
    ContentHashCache hashes,
    ChapterTextStore text) : IBookRegistry
{
    public async Task<EnrolledBook?> GetAsync(BookRef book, CancellationToken ct = default)
    {
        var row = await ReadRowAsync(book, ct);
        return row is null ? null : await ResolveAsync(row, ct);
    }

    public async Task<IReadOnlyList<EnrolledBook>> ListAsync(CancellationToken ct = default)
    {
        var rows = new List<Row>();

        await using (var conn = db.OpenConnection())
        await using (var cmd = conn.CreateCommand())
        {
            // Most recently opened first, never-opened last — the shelf order a
            // reader expects, and the reason last_opened_at exists.
            cmd.CommandText = $"SELECT {Columns} FROM r2_book ORDER BY last_opened_at DESC NULLS LAST, title";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadRow(reader));
        }

        var books = new List<EnrolledBook>(rows.Count);
        foreach (var row in rows) books.Add(await ResolveAsync(row, ct));
        return books;
    }

    public async Task<EnrolledBook> EnrolAsync(
        BookRef book, string fileName, string title, IReadOnlyList<string> authors,
        string lensKey, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO r2_book (book_id, file_name, title, authors_json, lens_key, added_at)
            VALUES ($id, $file, $title, $authors, $lens, $now)
            ON CONFLICT(book_id) DO UPDATE SET
                file_name = excluded.file_name,
                title     = excluded.title,
                authors_json = excluded.authors_json
            """;
        cmd.Parameters.AddWithValue("$id", book.Value);
        cmd.Parameters.AddWithValue("$file", fileName);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$authors", StorageConventions.Serialize(authors));
        cmd.Parameters.AddWithValue("$lens", lensKey);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());
        await cmd.ExecuteNonQueryAsync(ct);

        // Re-enrolling deliberately keeps the existing lens: a book already being
        // read as Fiction should not silently revert because it was re-added.
        return (await GetAsync(book, ct))!;
    }

    public async Task<bool> SetLensAsync(BookRef book, string lensKey, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE r2_book SET lens_key = $lens WHERE book_id = $id";
        cmd.Parameters.AddWithValue("$lens", lensKey);
        cmd.Parameters.AddWithValue("$id", book.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchOpenedAsync(BookRef book, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE r2_book SET last_opened_at = $now WHERE book_id = $id";
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());
        cmd.Parameters.AddWithValue("$id", book.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Un-enrols a book: the row goes, and the cascade takes its artifacts,
    /// reading positions, and bookmarks with it. The extracted text goes too —
    /// safe because an identical hash is the same book, so nothing else needs it.
    /// Vocabulary is untouched by design.
    /// </summary>
    public async Task<bool> RemoveAsync(BookRef book, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM r2_book WHERE book_id = $id";
        cmd.Parameters.AddWithValue("$id", book.Value);
        var removed = await cmd.ExecuteNonQueryAsync(ct) > 0;

        if (removed) text.Delete(book);
        return removed;
    }

    // ─── resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Turns a stored row into a usable book, repairing a stale file name.
    ///
    /// <para>Identity is the content hash, so a rename never loses a book's work —
    /// but the stored path does go stale. On a miss we look for a file whose
    /// contents match and update the row in place; if none does, the book is
    /// reported unavailable and <b>every artifact is kept</b>. Discarding a
    /// novel's accumulated story model because somebody moved a file is exactly
    /// the avoidable loss this design exists to prevent.</para>
    /// </summary>
    private async Task<EnrolledBook> ResolveAsync(Row row, CancellationToken ct)
    {
        if (library.Exists(row.FileName)) return row.ToBook(isAvailable: true);

        var relocated = await hashes.FindFileAsync(row.Book, ct);
        if (relocated is null)
        {
            Log.Information(
                "[reader2] {Title} is unavailable — no library file matches {Book}. Artifacts kept.",
                row.Title, row.Book);
            return row.ToBook(isAvailable: false);
        }

        Log.Information(
            "[reader2] {Title} moved: {Old} → {New}. Re-pointing; artifacts unaffected.",
            row.Title, row.FileName, relocated);

        await using (var conn = db.OpenConnection())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE r2_book SET file_name = $file WHERE book_id = $id";
            cmd.Parameters.AddWithValue("$file", relocated);
            cmd.Parameters.AddWithValue("$id", row.Book.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return (row with { FileName = relocated }).ToBook(isAvailable: true);
    }

    // ─── row plumbing ────────────────────────────────────────────────────

    private const string Columns =
        "book_id, file_name, title, authors_json, lens_key, added_at, last_opened_at";

    private sealed record Row(
        BookRef Book, string FileName, string Title, string AuthorsJson,
        string LensKey, DateTime AddedAt, DateTime? LastOpenedAt)
    {
        public EnrolledBook ToBook(bool isAvailable) => new(
            Book, FileName, Title,
            StorageConventions.Deserialize<string[]>(AuthorsJson) ?? [],
            LensKey, AddedAt, LastOpenedAt, isAvailable);
    }

    private static Row ReadRow(SqliteDataReader r) => new(
        BookRef.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), StorageConventions.ParseUtc(r.GetString(5)),
        r.IsDBNull(6) ? null : StorageConventions.ParseUtc(r.GetString(6)));

    private async Task<Row?> ReadRowAsync(BookRef book, CancellationToken ct)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM r2_book WHERE book_id = $id";
        cmd.Parameters.AddWithValue("$id", book.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }
}
