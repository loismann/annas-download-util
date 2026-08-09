using Microsoft.Data.Sqlite;

namespace AnnasArchive.API.Data;

/// <summary>
/// The single SQLite database for user state (see DOCS/reference/PROJECT_AUDIT.md §8.6/§9.0):
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

            CREATE TABLE IF NOT EXISTS spotify_inventory_meta (
                owner_hash        TEXT PRIMARY KEY,
                playlists_json    TEXT NOT NULL,
                last_inventory_at TEXT NOT NULL,
                full_inventory_at TEXT
            );

            CREATE TABLE IF NOT EXISTS spotify_playlist_cache (
                owner_hash       TEXT NOT NULL,
                playlist_id      TEXT NOT NULL,
                playlist_json    TEXT NOT NULL,
                access           TEXT NOT NULL,
                snapshot_id      TEXT,
                items_snapshot_id TEXT,
                items_json       TEXT,
                inventory_at     TEXT NOT NULL,
                items_updated_at TEXT,
                PRIMARY KEY (owner_hash, playlist_id)
            );

            CREATE TABLE IF NOT EXISTS spotify_inventory_job (
                owner_hash          TEXT PRIMARY KEY,
                job_id              TEXT,
                state               TEXT NOT NULL,
                total_playlists     INTEGER NOT NULL,
                processed_playlists INTEGER NOT NULL,
                readable_playlists  INTEGER NOT NULL,
                partial_playlists   INTEGER NOT NULL,
                unreadable_playlists INTEGER NOT NULL,
                started_at          TEXT,
                updated_at          TEXT,
                completed_at        TEXT,
                message             TEXT
            );

            CREATE TABLE IF NOT EXISTS spotify_known_music_override (
                owner_hash     TEXT NOT NULL,
                kind           TEXT NOT NULL,
                normalized_key TEXT NOT NULL,
                display_name   TEXT NOT NULL,
                is_known       INTEGER NOT NULL,
                updated_at     TEXT NOT NULL,
                PRIMARY KEY (owner_hash, kind, normalized_key)
            );

            CREATE TABLE IF NOT EXISTS spotify_signal_cache (
                owner_hash TEXT NOT NULL,
                cache_key  TEXT NOT NULL,
                json       TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (owner_hash, cache_key)
            );

            CREATE TABLE IF NOT EXISTS spotify_change_plan (
                owner_hash TEXT NOT NULL,
                plan_id    TEXT NOT NULL,
                status     TEXT NOT NULL,
                json       TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (owner_hash, plan_id)
            );

            CREATE TABLE IF NOT EXISTS spotify_audit_event (
                owner_hash TEXT NOT NULL,
                event_id   TEXT NOT NULL,
                plan_id    TEXT NOT NULL,
                kind       TEXT NOT NULL,
                at_utc     TEXT NOT NULL,
                json       TEXT NOT NULL,
                PRIMARY KEY (owner_hash, event_id)
            );

            CREATE TABLE IF NOT EXISTS spotify_discovery_draft (
                owner_hash TEXT NOT NULL,
                draft_id   TEXT NOT NULL,
                json       TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (owner_hash, draft_id)
            );

            CREATE TABLE IF NOT EXISTS audiobook_request (
                listenarr_id        INTEGER PRIMARY KEY,
                asin                TEXT NOT NULL UNIQUE,
                isbn_json           TEXT NOT NULL DEFAULT '[]',
                title_snapshot      TEXT NOT NULL,
                author_snapshot     TEXT NOT NULL,
                abs_item_id         TEXT,
                last_observed_status TEXT NOT NULL,
                last_error          TEXT,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audiobook_request_user (
                listenarr_id INTEGER NOT NULL,
                app_user_id  TEXT NOT NULL,
                owner_label  TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                PRIMARY KEY (listenarr_id, app_user_id),
                FOREIGN KEY (listenarr_id) REFERENCES audiobook_request(listenarr_id)
                    ON DELETE CASCADE
            );

            -- ─── Ebook Reader II (DOCS/features/EBOOK_READER_II.md §7.1) ───
            -- Wholly separate from Reader I, which keeps its state in ai-cache
            -- files. The r2_ prefix makes retirement of either side a clean drop.

            CREATE TABLE IF NOT EXISTS r2_book (
                book_id        TEXT PRIMARY KEY,   -- 16 hex of SHA-256(file bytes)
                file_name      TEXT NOT NULL,
                title          TEXT NOT NULL,
                authors_json   TEXT NOT NULL,
                lens_key       TEXT NOT NULL,
                added_at       TEXT NOT NULL,
                last_opened_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_r2_book_file ON r2_book(file_name);

            -- chapter/ordinal/subkey use -1/'' sentinels rather than NULL because
            -- SQLite treats NULLs as DISTINCT in a UNIQUE constraint: a nullable
            -- chapter would silently admit duplicate book-scoped rows.
            CREATE TABLE IF NOT EXISTS r2_artifact (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                book_id           TEXT    NOT NULL REFERENCES r2_book(book_id) ON DELETE CASCADE,
                lens_key          TEXT    NOT NULL,
                kind              TEXT    NOT NULL,
                chapter           INTEGER NOT NULL DEFAULT -1,
                ordinal           INTEGER NOT NULL DEFAULT -1,
                subkey            TEXT    NOT NULL DEFAULT '',
                schema_version    INTEGER NOT NULL,
                prompt_version    INTEGER NOT NULL,
                model             TEXT    NOT NULL,
                content_json      TEXT    NOT NULL,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                created_at        TEXT    NOT NULL,
                UNIQUE(book_id, lens_key, kind, chapter, ordinal, subkey)
            );
            CREATE INDEX IF NOT EXISTS ix_r2_artifact_lookup
                ON r2_artifact(book_id, lens_key, kind, chapter);

            -- No FK to r2_book on purpose: a term the reader knows must survive
            -- un-enrolling the book they first met it in.
            CREATE TABLE IF NOT EXISTS r2_vocabulary (
                user_id            TEXT NOT NULL,
                term_norm          TEXT NOT NULL,
                term_display       TEXT NOT NULL,
                state              TEXT NOT NULL,   -- known | studying
                definition         TEXT,
                first_seen_book_id TEXT,
                updated_at         TEXT NOT NULL,
                PRIMARY KEY (user_id, term_norm)
            );

            CREATE TABLE IF NOT EXISTS r2_reading_position (
                book_id     TEXT    NOT NULL REFERENCES r2_book(book_id) ON DELETE CASCADE,
                user_id     TEXT    NOT NULL,
                chapter     INTEGER NOT NULL,
                word_offset INTEGER NOT NULL DEFAULT 0,
                updated_at  TEXT    NOT NULL,
                PRIMARY KEY (book_id, user_id)
            );

            -- Its own table rather than riding reading position: a reader has many
            -- bookmarks and exactly one position.
            CREATE TABLE IF NOT EXISTS r2_bookmark (
                id          TEXT    PRIMARY KEY,
                book_id     TEXT    NOT NULL REFERENCES r2_book(book_id) ON DELETE CASCADE,
                user_id     TEXT    NOT NULL,
                chapter     INTEGER NOT NULL,
                word_offset INTEGER NOT NULL,
                label       TEXT,
                created_at  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_r2_bookmark_book_user
                ON r2_bookmark(book_id, user_id);

            CREATE TABLE IF NOT EXISTS r2_reading_preferences (
                user_id     TEXT PRIMARY KEY,
                font_family TEXT    NOT NULL,
                font_size   INTEGER NOT NULL,
                theme       TEXT    NOT NULL,
                split_ratio REAL    NOT NULL,
                updated_at  TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "spotify_inventory_meta", "full_inventory_at", "TEXT");

        // Per-person dismissal of a finished-but-unwanted request. Lives on the
        // attribution row rather than the request, so one person clearing a failed
        // download from their own library view does not hide it from everyone else
        // who asked for the same book.
        EnsureColumn(conn, "audiobook_request_user", "dismissed_at", "TEXT");
    }

    /// <summary>
    /// Opens a connection with foreign keys enforced.
    ///
    /// <para>SQLite defaults <c>foreign_keys</c> to <b>off</b>, and the setting is
    /// per-connection rather than stored with the database — so without this every
    /// <c>ON DELETE CASCADE</c> in the schema above is decorative, and deleting a
    /// parent row silently orphans its children instead of removing them.</para>
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private static void EnsureColumn(
        SqliteConnection connection, string table, string column, string declaration)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
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

    /// <summary>Deletes one state document. Missing keys are a successful no-op.</summary>
    public void DeleteState(string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
