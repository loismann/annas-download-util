using AnnasArchive.API.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The schema half of checkpoint 1a. Uses a real SQLite file rather than a mock,
/// because the behaviour being verified — cascade deletes and UNIQUE with
/// sentinels — is SQLite's, not ours.
/// </summary>
public sealed class Reader2SchemaTests : IDisposable
{
    private readonly string _dir;
    private readonly AppDatabase _db;

    public Reader2SchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "r2-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _db = new AppDatabase(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "app.db")
            })
            .Build());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    [Theory]
    [InlineData("r2_book")]
    [InlineData("r2_artifact")]
    [InlineData("r2_vocabulary")]
    [InlineData("r2_reading_position")]
    [InlineData("r2_bookmark")]
    [InlineData("r2_reading_preferences")]
    public void The_reader2_tables_are_created(string table)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);

        Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1, $"{table} should exist");
    }

    /// <summary>
    /// SQLite defaults foreign_keys OFF and the setting is per-connection, so
    /// without the PRAGMA every ON DELETE CASCADE in the schema is decorative.
    /// </summary>
    [Fact]
    public void Foreign_keys_are_enforced_on_every_connection()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys";

        Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1);
    }

    [Fact]
    public void Deleting_a_book_cascades_to_its_artifacts_positions_and_bookmarks()
    {
        using var conn = _db.OpenConnection();
        InsertBook(conn, "a1b2c3d4e5f60718");
        InsertArtifact(conn, "a1b2c3d4e5f60718", "fiction", "chapter-summary", chapter: 1);
        InsertPosition(conn, "a1b2c3d4e5f60718", "paul");
        InsertBookmark(conn, "a1b2c3d4e5f60718", "paul");

        Execute(conn, "DELETE FROM r2_book WHERE book_id = 'a1b2c3d4e5f60718'");

        Count(conn, "r2_artifact").Should().Be(0);
        Count(conn, "r2_reading_position").Should().Be(0);
        Count(conn, "r2_bookmark").Should().Be(0);
    }

    /// <summary>
    /// Vocabulary deliberately has no FK: a term the reader knows must outlive
    /// the book they first met it in.
    /// </summary>
    [Fact]
    public void Deleting_a_book_leaves_the_readers_vocabulary_alone()
    {
        using var conn = _db.OpenConnection();
        InsertBook(conn, "a1b2c3d4e5f60718");
        Execute(conn, """
            INSERT INTO r2_vocabulary (user_id, term_norm, term_display, state, first_seen_book_id, updated_at)
            VALUES ('paul', 'reification', 'reification', 'known', 'a1b2c3d4e5f60718', '2026-08-09T00:00:00Z')
            """);

        Execute(conn, "DELETE FROM r2_book WHERE book_id = 'a1b2c3d4e5f60718'");

        Count(conn, "r2_vocabulary").Should().Be(1);
    }

    /// <summary>
    /// The sentinel rule. With NULLs here SQLite would treat each row as
    /// distinct and happily store the same book-scoped artifact many times.
    /// </summary>
    [Fact]
    public void The_same_book_scoped_artifact_cannot_be_inserted_twice()
    {
        using var conn = _db.OpenConnection();
        InsertBook(conn, "a1b2c3d4e5f60718");
        InsertArtifact(conn, "a1b2c3d4e5f60718", "fiction", "story-model");

        var act = () => InsertArtifact(conn, "a1b2c3d4e5f60718", "fiction", "story-model");

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(19); // constraint
    }

    [Fact]
    public void The_same_chapter_under_two_lenses_is_two_rows()
    {
        using var conn = _db.OpenConnection();
        InsertBook(conn, "a1b2c3d4e5f60718");

        InsertArtifact(conn, "a1b2c3d4e5f60718", "fiction", "chapter-summary", chapter: 3);
        InsertArtifact(conn, "a1b2c3d4e5f60718", "military", "chapter-summary", chapter: 3);

        Count(conn, "r2_artifact").Should().Be(2);
    }

    [Fact]
    public void An_artifact_cannot_reference_a_book_that_does_not_exist()
    {
        using var conn = _db.OpenConnection();

        var act = () => InsertArtifact(conn, "ffffffffffffffff", "fiction", "chapter-summary", chapter: 1);

        act.Should().Throw<SqliteException>();
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static void InsertBook(SqliteConnection conn, string bookId) => Execute(conn, $"""
        INSERT INTO r2_book (book_id, file_name, title, authors_json, lens_key, added_at)
        VALUES ('{bookId}', 'book.epub', 'A Book', '[]', 'literary', '2026-08-09T00:00:00Z')
        """);

    private const string EmptyJson = "{}";

    private static void InsertArtifact(
        SqliteConnection conn, string bookId, string lens, string kind, int chapter = -1) => Execute(conn, $"""
        INSERT INTO r2_artifact
            (book_id, lens_key, kind, chapter, ordinal, subkey,
             schema_version, prompt_version, model, content_json, created_at)
        VALUES ('{bookId}', '{lens}', '{kind}', {chapter}, -1, '',
                1, 1, 'test-model', '{EmptyJson}', '2026-08-09T00:00:00Z')
        """);

    private static void InsertPosition(SqliteConnection conn, string bookId, string user) => Execute(conn, $"""
        INSERT INTO r2_reading_position (book_id, user_id, chapter, word_offset, updated_at)
        VALUES ('{bookId}', '{user}', 1, 0, '2026-08-09T00:00:00Z')
        """);

    private static void InsertBookmark(SqliteConnection conn, string bookId, string user) => Execute(conn, $"""
        INSERT INTO r2_bookmark (id, book_id, user_id, chapter, word_offset, created_at)
        VALUES ('{Guid.NewGuid():N}', '{bookId}', '{user}', 1, 120, '2026-08-09T00:00:00Z')
        """);

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
