using Microsoft.Data.Sqlite;

namespace AnnasArchive.API.Data;

/// <summary>
/// The single SQLite database for user state (see DOCS/PROJECT_AUDIT.md §8.6/§9.0):
/// book personalization lives in its own table, and single-document state
/// (media metadata, review progress) lives in the app_state key/value table.
/// Enrichment facts stay in the .meta.json sidecars, written only by the
/// watcher/download flow — the two can no longer overwrite each other because
/// they no longer share a file. Binaries (covers, EPUBs, caches) stay on the
/// filesystem; only paths/state belong here.
///
/// Database:Path points at the persistent /app/state mount in production;
/// the default keeps local dev runs inside an ignored ./state directory.
/// </summary>
public class AppDatabase
{
    private readonly string _dbPath;

    public AppDatabase(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("Database:Path");
        _dbPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "state", "app.db")
            : configured;

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS book_personalization (
                file_name         TEXT PRIMARY KEY,
                title             TEXT,
                authors_json      TEXT,
                primary_genre     TEXT,
                tags_json         TEXT,
                series            TEXT,
                goodreads_rating  REAL,
                personal_rating   INTEGER,
                reader_enabled    INTEGER,
                favorited_by_json TEXT,
                cull_reviewed_at  TEXT,
                updated_at        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_state (
                key        TEXT PRIMARY KEY,
                json       TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>Gets a JSON document from the app_state key/value table, or null if absent.</summary>
    public string? GetState(string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM app_state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Upserts a JSON document into the app_state key/value table.
    /// I/O failures propagate — a swallowed write here would report success
    /// to the client while the data never landed.</summary>
    public void SetState(string key, string json)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_state (key, json, updated_at) VALUES ($key, $json, $now)
            ON CONFLICT(key) DO UPDATE SET json = $json, updated_at = $now
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
