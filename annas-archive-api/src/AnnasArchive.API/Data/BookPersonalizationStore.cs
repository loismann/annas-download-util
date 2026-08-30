using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AnnasArchive.API.Data;

/// <summary>
/// Everything a user can edit about a library book. Every field is an
/// *override*: null means "no user opinion — fall back to the enrichment
/// value in the .meta.json sidecar". For the string fields, empty string
/// means "explicitly cleared by the user" (renders as null), which is a
/// different statement than never having touched it.
/// </summary>
public sealed class BookPersonalization
{
    public required string FileName { get; init; }
    public string? Title { get; set; }
    public string[]? Authors { get; set; }
    public string? PrimaryGenre { get; set; }
    public string[]? Tags { get; set; }
    public string? Series { get; set; }
    public double? GoodreadsRating { get; set; }
    public int? PersonalRating { get; set; }
    public string[]? FavoritedBy { get; set; }
    public DateTime? CullReviewedAt { get; set; }
}

/// <summary>
/// SQLite-backed store for user personalization of library books — the write
/// target for every edit endpoint. The enrichment watcher never touches this
/// table, which is what makes the old "re-enrichment clobbers user edits"
/// failure structurally impossible (DOCS/reference/PROJECT_AUDIT.md §8.6).
/// </summary>
public class BookPersonalizationStore
{
    private const string ImportedFlagKey = "book-personalization-imported";

    private readonly AppDatabase _db;
    private readonly object _writeLock = new();

    public BookPersonalizationStore(AppDatabase db)
    {
        _db = db;
    }

    /// <summary>Applies a string override: null = no opinion, "" = explicitly cleared.</summary>
    public static string? OverrideString(string? overrideValue, string? fallback) =>
        overrideValue is null ? fallback : (overrideValue.Length == 0 ? null : overrideValue);

    public Dictionary<string, BookPersonalization> LoadAll()
    {
        var result = new Dictionary<string, BookPersonalization>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM book_personalization";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadRow(reader);
            result[row.FileName] = row;
        }
        return result;
    }

    public BookPersonalization? Get(string fileName)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM book_personalization WHERE file_name = $fn";
        cmd.Parameters.AddWithValue("$fn", fileName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>Read-modify-write for one book's row (created on first edit).
    /// Serialized under a lock — with three household users, contention is
    /// effectively zero and this is far simpler than per-field UPSERT SQL.</summary>
    public BookPersonalization Update(string fileName, Action<BookPersonalization> mutate)
    {
        lock (_writeLock)
        {
            var row = Get(fileName) ?? new BookPersonalization { FileName = fileName };
            mutate(row);
            Upsert(row);
            return row;
        }
    }

    public void Delete(string fileName)
    {
        lock (_writeLock)
        {
            using var conn = _db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM book_personalization WHERE file_name = $fn";
            cmd.Parameters.AddWithValue("$fn", fileName);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Clears genre/tag overrides for every book (the admin "wipe genres" bulk
    /// operation). Meta-file fallbacks are cleared separately by the endpoint.</summary>
    public void ClearGenreFields()
    {
        lock (_writeLock)
        {
            using var conn = _db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE book_personalization SET primary_genre = NULL, tags_json = NULL, updated_at = $now";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One-time import of user fields from the existing .meta.json sidecars, so
    /// personalization made before this store existed carries over. Guarded by a
    /// flag in app_state; cheap no-op on every rebuild after the first.
    /// Title/authors are NOT imported — in the sidecars they're indistinguishable
    /// from enrichment output, and importing them would freeze enrichment
    /// improvements behind a phantom "user override".
    /// </summary>
    public void ImportFromMetaFilesIfNeeded(string libraryRoot)
    {
        if (_db.GetState(ImportedFlagKey) != null)
            return;

        lock (_writeLock)
        {
            if (_db.GetState(ImportedFlagKey) != null)
                return;

            var imported = 0;
            if (Directory.Exists(libraryRoot))
            {
                var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
                foreach (var metaFile in Directory.GetFiles(libraryRoot, "*.meta.json"))
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(File.ReadAllText(metaFile), jsonOptions);
                        if (meta == null)
                            continue;

                        var hasUserFields =
                            !string.IsNullOrWhiteSpace(meta.PrimaryGenre) ||
                            (meta.Tags?.Length ?? 0) > 0 ||
                            !string.IsNullOrWhiteSpace(meta.Series) ||
                            meta.PersonalRating.HasValue ||
                            (meta.FavoritedBy?.Length ?? 0) > 0 ||
                            meta.CullReviewedAt.HasValue;

                        if (!hasUserFields)
                            continue;

                        Upsert(new BookPersonalization
                        {
                            FileName = meta.FileName,
                            PrimaryGenre = string.IsNullOrWhiteSpace(meta.PrimaryGenre) ? null : meta.PrimaryGenre,
                            Tags = (meta.Tags?.Length ?? 0) > 0 ? meta.Tags : null,
                            Series = string.IsNullOrWhiteSpace(meta.Series) ? null : meta.Series,
                            PersonalRating = meta.PersonalRating,
                            FavoritedBy = (meta.FavoritedBy?.Length ?? 0) > 0 ? meta.FavoritedBy : null,
                            CullReviewedAt = meta.CullReviewedAt
                        });
                        imported++;
                    }
                    catch
                    {
                        // Malformed sidecar — same tolerance as the index builder.
                    }
                }
            }

            _db.SetState(ImportedFlagKey, JsonSerializer.Serialize(new { importedAt = DateTime.UtcNow, books = imported }));
            Log.Information("[BookPersonalization] One-time import complete: {Count} books carried over from meta sidecars", imported);
        }
    }

    private void Upsert(BookPersonalization row)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_personalization
                (file_name, title, authors_json, primary_genre, tags_json, series,
                 goodreads_rating, personal_rating, favorited_by_json,
                 cull_reviewed_at, updated_at)
            VALUES ($fn, $title, $authors, $genre, $tags, $series, $gr, $pr, $fav, $cull, $now)
            ON CONFLICT(file_name) DO UPDATE SET
                title = $title, authors_json = $authors, primary_genre = $genre,
                tags_json = $tags, series = $series, goodreads_rating = $gr,
                personal_rating = $pr,
                favorited_by_json = $fav, cull_reviewed_at = $cull, updated_at = $now
            """;
        cmd.Parameters.AddWithValue("$fn", row.FileName);
        cmd.Parameters.AddWithValue("$title", (object?)row.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$authors", row.Authors != null ? JsonSerializer.Serialize(row.Authors) : DBNull.Value);
        cmd.Parameters.AddWithValue("$genre", (object?)row.PrimaryGenre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", row.Tags != null ? JsonSerializer.Serialize(row.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("$series", (object?)row.Series ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gr", (object?)row.GoodreadsRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr", (object?)row.PersonalRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fav", row.FavoritedBy != null ? JsonSerializer.Serialize(row.FavoritedBy) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cull", row.CullReviewedAt.HasValue ? row.CullReviewedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static BookPersonalization ReadRow(SqliteDataReader reader)
    {
        string? Str(string col) => reader[col] is string s ? s : null;
        string[]? Arr(string col)
        {
            var json = Str(col);
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<string[]>(json); } catch { return null; }
        }

        return new BookPersonalization
        {
            FileName = (string)reader["file_name"],
            Title = Str("title"),
            Authors = Arr("authors_json"),
            PrimaryGenre = Str("primary_genre"),
            Tags = Arr("tags_json"),
            Series = Str("series"),
            GoodreadsRating = reader["goodreads_rating"] is double d ? d : null,
            PersonalRating = reader["personal_rating"] is long l ? (int)l : null,
            FavoritedBy = Arr("favorited_by_json"),
            CullReviewedAt = Str("cull_reviewed_at") is string cull &&
                DateTime.TryParse(cull, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt
                    : null
        };
    }
}
