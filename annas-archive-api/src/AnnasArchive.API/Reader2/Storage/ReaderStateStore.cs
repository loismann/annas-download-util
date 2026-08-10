using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Domain;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>Where one reader had got to in one book.</summary>
public sealed record ReadingPosition(int Chapter, int WordOffset, DateTime UpdatedAtUtc);

/// <summary>
/// How one reader wants the page to look.
///
/// <para>In the database rather than <c>localStorage</c>, which is where Reader I
/// keeps it — so settings follow a reader between devices instead of belonging to
/// a browser, and so two household members on one machine do not overwrite each
/// other.</para>
/// </summary>
public sealed record ReadingPreferences(
    string FontFamily = "serif",
    int FontSize = 18,
    string Theme = "light",
    double SplitRatio = 0.6);

/// <summary>
/// The small per-user rows: where the reader is, and how they like it to look.
///
/// <para>Not artifacts. An artifact describes the book and is shared across the
/// household; both of these describe a person and are theirs alone — which is
/// also why neither is keyed by lens.</para>
/// </summary>
public sealed class ReaderStateStore(AppDatabase db)
{
    public async Task<ReadingPosition?> GetPositionAsync(
        BookRef book, string userId, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT chapter, word_offset, updated_at FROM r2_reading_position
            WHERE book_id = $book AND user_id = $user
            """;
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$user", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new ReadingPosition(
                reader.GetInt32(0), reader.GetInt32(1), StorageConventions.ParseUtc(reader.GetString(2)))
            : null;
    }

    public async Task SetPositionAsync(
        BookRef book, string userId, int chapter, int wordOffset, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO r2_reading_position (book_id, user_id, chapter, word_offset, updated_at)
            VALUES ($book, $user, $chapter, $offset, $now)
            ON CONFLICT(book_id, user_id) DO UPDATE SET
                chapter = excluded.chapter,
                word_offset = excluded.word_offset,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$offset", wordOffset);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Stored preferences, or the defaults for a reader who has none.</summary>
    public async Task<ReadingPreferences> GetPreferencesAsync(string userId, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT font_family, font_size, theme, split_ratio FROM r2_reading_preferences
            WHERE user_id = $user
            """;
        cmd.Parameters.AddWithValue("$user", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new ReadingPreferences(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetDouble(3))
            : new ReadingPreferences();
    }

    public async Task SetPreferencesAsync(
        string userId, ReadingPreferences preferences, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO r2_reading_preferences
                (user_id, font_family, font_size, theme, split_ratio, updated_at)
            VALUES ($user, $font, $size, $theme, $split, $now)
            ON CONFLICT(user_id) DO UPDATE SET
                font_family = excluded.font_family,
                font_size   = excluded.font_size,
                theme       = excluded.theme,
                split_ratio = excluded.split_ratio,
                updated_at  = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$font", preferences.FontFamily);
        cmd.Parameters.AddWithValue("$size", preferences.FontSize);
        cmd.Parameters.AddWithValue("$theme", preferences.Theme);
        cmd.Parameters.AddWithValue("$split", preferences.SplitRatio);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
