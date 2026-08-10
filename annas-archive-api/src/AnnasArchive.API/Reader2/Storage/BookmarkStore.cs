using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Domain;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>A place one reader marked in one book.</summary>
public sealed record Bookmark(
    string Id, int Chapter, int WordOffset, string? Label, DateTime CreatedAtUtc);

/// <summary>
/// The reader's own marks in a book.
///
/// <para>Separate from <see cref="ReaderStateStore"/>, which holds the two
/// per-reader <i>singletons</i> — one position, one set of preferences, each a
/// plain upsert. Bookmarks are a keyed collection with their own identity and
/// removal, and folding a third shape into that file would leave one class doing
/// two unrelated jobs.</para>
///
/// <para>No <c>lens_key</c> column, by design. A bookmark marks a place in the
/// text, and the text does not change when the book type does — so switching
/// between a literary and a military reading keeps every mark.</para>
/// </summary>
public sealed class BookmarkStore(AppDatabase db)
{
    /// <summary>In reading order, which is the order a bookmark bar shows them.</summary>
    public async Task<IReadOnlyList<Bookmark>> ListAsync(
        BookRef book, string userId, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, chapter, word_offset, label, created_at FROM r2_bookmark
            WHERE book_id = $book AND user_id = $user
            ORDER BY chapter, word_offset
            """;
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$user", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var marks = new List<Bookmark>();

        while (await reader.ReadAsync(ct)) marks.Add(Read(reader));

        return marks;
    }

    /// <summary>
    /// Marks a place, or re-labels the mark already there.
    ///
    /// <para>Idempotent on position rather than insert-only: the control that
    /// calls this is a toggle on the page the reader is looking at, and a bar
    /// listing the same page twice reads as a defect. A unique index makes that
    /// true even for two devices pressing it at once — a read-then-insert would
    /// not.</para>
    /// </summary>
    public async Task<Bookmark> SaveAsync(
        BookRef book, string userId, int chapter, int wordOffset, string? label,
        CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO r2_bookmark (id, book_id, user_id, chapter, word_offset, label, created_at)
            VALUES ($id, $book, $user, $chapter, $offset, $label, $now)
            ON CONFLICT(book_id, user_id, chapter, word_offset) DO UPDATE SET
                label = excluded.label
            RETURNING id, chapter, word_offset, label, created_at
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$offset", wordOffset);
        cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return Read(reader);
    }

    /// <summary>
    /// Removes one mark. False when it is not this reader's, which the route
    /// answers as a 404 — a bookmark somebody else owns does not exist from here.
    /// </summary>
    public async Task<bool> RemoveAsync(
        BookRef book, string userId, string bookmarkId, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM r2_bookmark
            WHERE id = $id AND book_id = $book AND user_id = $user
            """;
        cmd.Parameters.AddWithValue("$id", bookmarkId);
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$user", userId);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static Bookmark Read(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        StorageConventions.ParseUtc(reader.GetString(4)));
}
