using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Reader2.Domain;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// <see cref="IArtifactStore"/> over the shared <see cref="AppDatabase"/>.
///
/// <para>Every artifact is one row in <c>r2_artifact</c>, so a book's whole AI
/// output is one <c>DELETE</c>, a lens switch is a <c>WHERE</c> clause, and
/// "which prompt produced this" is a column rather than a guess.</para>
/// </summary>
public sealed class SqliteArtifactStore(AppDatabase db) : IArtifactStore
{
    private const string SelectColumns =
        "id, lens_key, kind, chapter, ordinal, subkey, schema_version, prompt_version, " +
        "model, content_json, prompt_tokens, completion_tokens, created_at";

    public async Task<Stored<T>?> GetAsync<T>(
        ArtifactKey key, ArtifactVersions current, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM r2_artifact
            WHERE book_id = $book AND lens_key = $lens AND kind = $kind
              AND chapter = $chapter AND ordinal = $ordinal AND subkey = $subkey
            """;
        BindKey(cmd, key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var row = ReadRow(reader);
        await reader.CloseAsync();

        // Stale shape: unreadable by definition, so drop it rather than leave a
        // row that will fail to deserialise on every future read.
        if (row.SchemaVersion < current.Schema)
        {
            var removed = await DeleteByIdAsync(conn, row.Id, ct);
            Log.Information(
                "[reader2] Discarded {Kind} for {Book}: schema v{Stored} < v{Current} ({Removed} row)",
                key.Kind.Wire(), key.Book, row.SchemaVersion, current.Schema, removed);
            return null;
        }

        // Older prompt: a hit, marked. See Stored<T>.Stale — the content is valid
        // and already paid for, and the caller decides whether to replace it.
        return Materialise<T>(key, row) is { } stored
            ? stored with { Stale = row.PromptVersion < current.Prompt }
            : null;
    }

    public async Task PutAsync<T>(
        ArtifactKey key, T content, ArtifactProvenance provenance, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();

        // The WHERE on the upsert is the other half of the rollback promise:
        // refusing to delete newer work but letting the next write clobber it
        // would protect nothing.
        cmd.CommandText = """
            INSERT INTO r2_artifact
                (book_id, lens_key, kind, chapter, ordinal, subkey,
                 schema_version, prompt_version, model, content_json,
                 prompt_tokens, completion_tokens, created_at)
            VALUES ($book, $lens, $kind, $chapter, $ordinal, $subkey,
                    $schema, $prompt, $model, $json, $ptok, $ctok, $now)
            ON CONFLICT(book_id, lens_key, kind, chapter, ordinal, subkey) DO UPDATE SET
                schema_version    = excluded.schema_version,
                prompt_version    = excluded.prompt_version,
                model             = excluded.model,
                content_json      = excluded.content_json,
                prompt_tokens     = excluded.prompt_tokens,
                completion_tokens = excluded.completion_tokens,
                created_at        = excluded.created_at
            WHERE excluded.schema_version >= r2_artifact.schema_version
            """;
        BindKey(cmd, key);
        cmd.Parameters.AddWithValue("$schema", provenance.SchemaVersion);
        cmd.Parameters.AddWithValue("$prompt", provenance.PromptVersion);
        cmd.Parameters.AddWithValue("$model", provenance.Model);
        cmd.Parameters.AddWithValue("$json", StorageConventions.Serialize(content));
        cmd.Parameters.AddWithValue("$ptok", provenance.PromptTokens);
        cmd.Parameters.AddWithValue("$ctok", provenance.CompletionTokens);
        cmd.Parameters.AddWithValue("$now", StorageConventions.NowIso());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Stored<T>>> ListAsync<T>(
        ArtifactQuery query, ArtifactVersions current, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM r2_artifact
            WHERE book_id = $book AND lens_key = $lens AND kind = $kind
              AND ($chapter IS NULL OR chapter = $chapter)
            ORDER BY chapter, ordinal, subkey
            """;
        cmd.Parameters.AddWithValue("$book", query.Book.Value);
        cmd.Parameters.AddWithValue("$lens", query.LensKey);
        cmd.Parameters.AddWithValue("$kind", query.Kind.Wire());
        cmd.Parameters.AddWithValue("$chapter", (object?)query.Chapter ?? DBNull.Value);

        var results = new List<Stored<T>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = ReadRow(reader);

            // Same gates as GetAsync, minus the delete: silently removing rows
            // during a read-many is a surprise nobody asked for.
            if (row.SchemaVersion < current.Schema) continue;

            var stored = Materialise<T>(
                ArtifactKey.FromRow(query.Book, row.LensKey, row.Kind, row.Chapter, row.Ordinal, row.Subkey),
                row);

            if (stored is not null)
                results.Add(stored with { Stale = row.PromptVersion < current.Prompt });
        }

        return results;
    }

    public async Task<int> DeleteForBookAsync(BookRef book, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM r2_artifact WHERE book_id = $book";
        cmd.Parameters.AddWithValue("$book", book.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteStaleAsync(
        BookRef book, string lensKey, int belowPromptVersion, CancellationToken ct = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM r2_artifact
            WHERE book_id = $book AND lens_key = $lens AND prompt_version < $below
            """;
        cmd.Parameters.AddWithValue("$book", book.Value);
        cmd.Parameters.AddWithValue("$lens", lensKey);
        cmd.Parameters.AddWithValue("$below", belowPromptVersion);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── row plumbing ────────────────────────────────────────────────────

    private sealed record Row(
        long Id, string LensKey, ArtifactKind Kind, int Chapter, int Ordinal, string Subkey,
        int SchemaVersion, int PromptVersion, string Model, string ContentJson,
        int PromptTokens, int CompletionTokens, DateTime CreatedAt);

    private static Row ReadRow(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), ArtifactKinds.Parse(r.GetString(2)),
        r.GetInt32(3), r.GetInt32(4), r.GetString(5),
        r.GetInt32(6), r.GetInt32(7), r.GetString(8), r.GetString(9),
        r.GetInt32(10), r.GetInt32(11),
        StorageConventions.ParseUtc(r.GetString(12)));

    /// <summary>
    /// Deserialises a row, or returns null if it will not fit the current record.
    ///
    /// <para>The only way here is a row written by a newer build — a stale schema
    /// was already deleted above. Newer JSON usually still fits, since unknown
    /// properties are ignored; when it does not, a miss is the right answer and
    /// the row stays put for the newer build to find again.</para>
    /// </summary>
    private static Stored<T>? Materialise<T>(ArtifactKey key, Row row)
    {
        try
        {
            var content = StorageConventions.Deserialize<T>(row.ContentJson);
            if (content is null) return null;

            return new Stored<T>(
                key,
                content,
                new ArtifactProvenance(
                    row.SchemaVersion, row.PromptVersion, row.Model,
                    row.PromptTokens, row.CompletionTokens),
                row.CreatedAt);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex,
                "[reader2] {Kind} for {Book} did not deserialise (schema v{Schema}); treating as a miss and keeping the row",
                key.Kind.Wire(), key.Book, row.SchemaVersion);
            return null;
        }
    }

    private static async Task<int> DeleteByIdAsync(SqliteConnection conn, long id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM r2_artifact WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindKey(SqliteCommand cmd, ArtifactKey key)
    {
        cmd.Parameters.AddWithValue("$book", key.Book.Value);
        cmd.Parameters.AddWithValue("$lens", key.LensKey);
        cmd.Parameters.AddWithValue("$kind", key.Kind.Wire());
        cmd.Parameters.AddWithValue("$chapter", key.Chapter);
        cmd.Parameters.AddWithValue("$ordinal", key.Ordinal);
        cmd.Parameters.AddWithValue("$subkey", key.Subkey);
    }
}
