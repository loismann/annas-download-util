using Microsoft.Data.Sqlite;

namespace AnnasArchive.API.Data;

/// <summary>
/// Changes an <i>existing</i> database needs that the CREATE statements cannot make.
///
/// <para>Every table in <see cref="AppDatabase"/> is <c>CREATE TABLE IF NOT EXISTS</c>,
/// so editing one only changes what a <b>fresh</b> database gets — an existing one keeps
/// whatever it was born with, forever. Everything here exists to close that gap, and it
/// lives apart from the schema because a constructor that is also a migration script
/// hides both.</para>
///
/// <para>Each step is written to be safe to run on every startup: it checks the current
/// shape first and does nothing if the change is already in place.</para>
/// </summary>
internal static class SchemaMigrations
{
    /// <summary>Runs every migration, in order, against an open connection.</summary>
    public static void Apply(SqliteConnection conn)
    {
        EnsureColumn(conn, "spotify_inventory_meta", "full_inventory_at", "TEXT");

        // Per-person dismissal of a finished-but-unwanted request. Lives on the
        // attribution row rather than the request, so one person clearing a failed
        // download from their own library view does not hide it from everyone else
        // who asked for the same book.
        EnsureColumn(conn, "audiobook_request_user", "dismissed_at", "TEXT");

        // Reader I is retired. Its per-book "show this in the reader" flag has no
        // reader left to mean anything to — the reader now keeps its own shelf in
        // r2_book — so drop it rather than leave a column nothing reads or writes.
        DropColumn(conn, "book_personalization", "reader_enabled");

        // The library treats file names case-insensitively everywhere else —
        // LoadAll hands back an OrdinalIgnoreCase dictionary — but this column was
        // a plain TEXT PRIMARY KEY, so the SQL matched case-sensitively. Two
        // casings of one name made two rows, and LoadAll silently kept whichever
        // it read last: somebody's ratings and favourites vanished with no error
        // and no second copy, since this table exists precisely because the
        // sidecars get overwritten.
        MigrateFileNameToCaseInsensitive(conn);
    }

    /// <summary>
    /// Rebuilds <c>book_personalization</c> with a case-insensitive primary key.
    ///
    /// <para>SQLite cannot alter a column's collation, so this is the standard
    /// rebuild: new table, copy, swap. Case-collided rows are merged rather than
    /// dropped — <c>INSERT OR REPLACE</c> fed rows oldest-first means the most
    /// recently edited copy survives, which is the only defensible answer when two
    /// rows both claim to be one book.</para>
    ///
    /// <para>Detected from the stored DDL rather than a version flag, so it is a
    /// no-op on a database that already has it and on every fresh one.</para>
    /// </summary>
    private static void MigrateFileNameToCaseInsensitive(SqliteConnection connection)
    {
        using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='book_personalization'";
            if (inspect.ExecuteScalar() is not string ddl ||
                ddl.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase))
                return;
        }

        const string columns =
            "file_name, title, authors_json, primary_genre, tags_json, series, " +
            "goodreads_rating, personal_rating, favorited_by_json, cull_reviewed_at, updated_at";

        using var migrate = connection.CreateCommand();
        migrate.CommandText = $"""
            PRAGMA foreign_keys=off;
            BEGIN TRANSACTION;

            CREATE TABLE book_personalization_migrated (
                file_name         TEXT PRIMARY KEY COLLATE NOCASE,
                title             TEXT,
                authors_json      TEXT,
                primary_genre     TEXT,
                tags_json         TEXT,
                series            TEXT,
                goodreads_rating  REAL,
                personal_rating   INTEGER,
                favorited_by_json TEXT,
                cull_reviewed_at  TEXT,
                updated_at        TEXT NOT NULL
            );

            INSERT OR REPLACE INTO book_personalization_migrated ({columns})
            SELECT {columns} FROM book_personalization ORDER BY updated_at ASC;

            DROP TABLE book_personalization;
            ALTER TABLE book_personalization_migrated RENAME TO book_personalization;

            COMMIT;
            PRAGMA foreign_keys=on;
            """;
        migrate.ExecuteNonQuery();

        Serilog.Log.Information(
            "[AppDatabase] Rebuilt book_personalization with a case-insensitive file_name");
    }

    /// <summary>
    /// Removes a column if the database still has one. The inverse of
    /// <see cref="EnsureColumn"/>: taking it out of the CREATE statement above only
    /// changes what a <i>fresh</i> database gets, because every table there is
    /// CREATE TABLE IF NOT EXISTS — an existing database would keep the column
    /// forever without this.
    /// </summary>
    private static void DropColumn(SqliteConnection connection, string table, string column)
    {
        if (!HasColumn(connection, table, column))
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column}";
        alter.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnsureColumn(
        SqliteConnection connection, string table, string column, string declaration)
    {
        if (HasColumn(connection, table, column))
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
    }
}
