using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Data;

/// <summary>
/// The write target for every library edit.
///
/// <para>239 lines with no test naming it, and it is the table that exists
/// specifically so re-enrichment can no longer clobber user edits — the whole
/// point being that this data survives things that overwrite the sidecars. A
/// defect here loses somebody's ratings and favourites permanently, with no
/// second copy to recover from.</para>
///
/// <para>Run against a real SQLite file in a temp directory, because the parts
/// worth pinning are the ones a fake would paper over: what the round trip does
/// to arrays and dates, how the tri-state override is stored, and how the SQL
/// treats a file name that differs only in case.</para>
/// </summary>
public sealed class BookPersonalizationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bookpers-tests", Guid.NewGuid().ToString("N"));

    private readonly AppDatabase _db;
    private readonly BookPersonalizationStore _store;

    public BookPersonalizationStoreTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "app.db")
            })
            .Build();

        _db = new AppDatabase(config);
        _store = new BookPersonalizationStore(_db);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    // ------------------------------------------------------- OverrideString

    /// <summary>
    /// The tri-state that makes "clear this field" expressible at all. Null means
    /// the user said nothing and the enriched value stands; empty string means they
    /// actively emptied it. Collapsing the two would make a cleared field silently
    /// refill itself from enrichment on the next pass.
    /// </summary>
    [Theory]
    [InlineData(null, "Enriched", "Enriched")]   // no opinion — enrichment wins
    [InlineData("", "Enriched", null)]           // explicitly cleared — stays empty
    [InlineData("Mine", "Enriched", "Mine")]     // an override
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    public void AnOverrideDistinguishesNoOpinionFromExplicitlyCleared(
        string? over, string? fallback, string? expected)
    {
        BookPersonalizationStore.OverrideString(over, fallback).Should().Be(expected);
    }

    /// <summary>Whitespace is a value, not a clear — only a truly empty string clears.</summary>
    [Fact]
    public void WhitespaceIsAnOverrideNotAClear()
    {
        BookPersonalizationStore.OverrideString(" ", "Enriched").Should().Be(" ");
    }

    // ---------------------------------------------------------- round trip

    [Fact]
    public void AnUneditedBookHasNoRow()
    {
        _store.Get("never-touched.epub").Should().BeNull();
    }

    [Fact]
    public void TheFirstEditCreatesTheRow()
    {
        _store.Update("a.epub", r => r.PersonalRating = 4);

        _store.Get("a.epub")!.PersonalRating.Should().Be(4);
    }

    /// <summary>
    /// Read-modify-write, not overwrite. Rating a book must not wipe the tags
    /// someone set last week — and the upsert writes every column, so this is only
    /// true because <c>Update</c> loads the existing row first.
    /// </summary>
    [Fact]
    public void EditingOneFieldLeavesTheRestOfTheRowAlone()
    {
        _store.Update("a.epub", r =>
        {
            r.Tags = ["Hardback", "Signed"];
            r.Series = "A Series";
            r.PersonalRating = 5;
        });

        _store.Update("a.epub", r => r.PersonalRating = 3);

        var row = _store.Get("a.epub")!;
        row.PersonalRating.Should().Be(3);
        row.Tags.Should().BeEquivalentTo("Hardback", "Signed");
        row.Series.Should().Be("A Series");
    }

    /// <summary>Arrays are stored as JSON, so their round trip is worth proving rather than assuming.</summary>
    [Fact]
    public void ArraysSurviveTheRoundTrip()
    {
        _store.Update("a.epub", r =>
        {
            r.Authors = ["Ursula K. Le Guin", "Someone Else"];
            r.Tags = [];
            r.FavoritedBy = ["Mom"];
        });

        var row = _store.Get("a.epub")!;
        row.Authors.Should().BeEquivalentTo("Ursula K. Le Guin", "Someone Else");
        row.Tags.Should().BeEmpty("an empty array is 'no tags', which is different from 'never set'");
        row.FavoritedBy.Should().BeEquivalentTo("Mom");
    }

    /// <summary>
    /// The cull review date decides whether the daily review modal shows a book
    /// again, so losing sub-second precision or the kind flag would resurface books
    /// already dealt with.
    /// </summary>
    [Fact]
    public void TheCullReviewDateSurvivesWithItsPrecision()
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);

        _store.Update("a.epub", r => r.CullReviewedAt = when);

        _store.Get("a.epub")!.CullReviewedAt.Should().Be(when);
    }

    [Fact]
    public void ADeletedRowIsGone()
    {
        _store.Update("a.epub", r => r.PersonalRating = 4);

        _store.Delete("a.epub");

        _store.Get("a.epub").Should().BeNull();
    }

    [Fact]
    public void DeletingABookThatWasNeverEditedIsHarmless()
    {
        var act = () => _store.Delete("nothing.epub");

        act.Should().NotThrow();
    }

    /// <summary>
    /// A corrupted JSON array must read as "not set" rather than taking down the
    /// whole library index — one bad row would otherwise hide every book.
    /// </summary>
    [Fact]
    public void ACorruptedArrayColumnReadsAsUnsetRatherThanThrowing()
    {
        _store.Update("a.epub", r => r.Tags = ["Fine"]);

        using (var conn = _db.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE book_personalization SET tags_json = 'not json' WHERE file_name = 'a.epub'";
            cmd.ExecuteNonQuery();
        }

        var act = () => _store.Get("a.epub");

        act.Should().NotThrow();
        act()!.Tags.Should().BeNull();
    }

    // -------------------------------------------------------------- LoadAll

    [Fact]
    public void LoadAllReturnsEveryEditedBookKeyedByFileName()
    {
        _store.Update("a.epub", r => r.PersonalRating = 4);
        _store.Update("b.epub", r => r.PersonalRating = 5);

        var all = _store.LoadAll();

        all.Should().HaveCount(2);
        all["b.epub"].PersonalRating.Should().Be(5);
    }

    [Fact]
    public void LoadAllIsEmptyBeforeAnyoneHasEditedAnything()
    {
        _store.LoadAll().Should().BeEmpty();
    }

    /// <summary>
    /// <c>LoadAll</c> hands back an <c>OrdinalIgnoreCase</c> dictionary, so the
    /// index can look a book up however its file name happens to be cased.
    /// </summary>
    [Fact]
    public void LoadAllLooksUpCaseInsensitively()
    {
        _store.Update("Book.epub", r => r.PersonalRating = 4);

        _store.LoadAll().Should().ContainKey("book.epub");
    }

    /// <summary>
    /// The SQL agrees with that dictionary.
    ///
    /// <para>It used to not. <c>file_name</c> was a plain <c>TEXT PRIMARY KEY</c>,
    /// so <c>Get</c> and <c>Update</c> matched case-sensitively while
    /// <c>LoadAll</c> matched case-insensitively. Two casings of one name made
    /// <b>two rows</b>, which <c>LoadAll</c> silently collapsed to whichever it
    /// read last — losing somebody's ratings and favourites with no error and no
    /// second copy, since this table exists precisely because the sidecars get
    /// overwritten. Fixed by <c>COLLATE NOCASE</c> on the column.</para>
    /// </summary>
    [Fact]
    public void TheSqlAgreesWithLoadAllAboutCasing()
    {
        _store.Update("Book.epub", r => r.PersonalRating = 4);

        _store.Get("book.epub").Should().NotBeNull("the row is the same book however it is cased");
        _store.Get("BOOK.EPUB")!.PersonalRating.Should().Be(4);
    }

    /// <summary>
    /// The consequence that matters: an edit under one casing lands on the same
    /// row, rather than creating a second one that hides the first.
    /// </summary>
    [Fact]
    public void EditingUnderADifferentCasingUpdatesTheSameRow()
    {
        _store.Update("Book.epub", r => { r.PersonalRating = 4; r.Series = "A Series"; });

        _store.Update("book.epub", r => r.PersonalRating = 1);

        _store.LoadAll().Should().HaveCount(1, "one book, one row");
        var row = _store.Get("Book.epub")!;
        row.PersonalRating.Should().Be(1);
        row.Series.Should().Be("A Series", "and it is a read-modify-write, not a replacement");
    }

    /// <summary>
    /// The migration for databases that already collected the duplicates.
    ///
    /// <para>Two rows for one book cannot both survive a case-insensitive primary
    /// key, so the merge keeps the most recently edited — the only defensible
    /// answer when both claim to be the same book. Built here against the
    /// pre-migration schema, then reopened so the migration runs.</para>
    /// </summary>
    [Fact]
    public void TheMigrationMergesRowsThatDifferOnlyInCaseKeepingTheNewest()
    {
        var dbPath = Path.Combine(_dir, "legacy.db");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE book_personalization (
                    file_name TEXT PRIMARY KEY, title TEXT, authors_json TEXT,
                    primary_genre TEXT, tags_json TEXT, series TEXT,
                    goodreads_rating REAL, personal_rating INTEGER,
                    favorited_by_json TEXT, cull_reviewed_at TEXT, updated_at TEXT NOT NULL);
                INSERT INTO book_personalization (file_name, personal_rating, updated_at)
                VALUES ('Book.epub', 3, '2026-01-01T00:00:00Z'),
                       ('book.epub', 5, '2026-06-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var migrated = new BookPersonalizationStore(new AppDatabase(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:Path"] = dbPath }).Build()));

        var all = migrated.LoadAll();
        all.Should().ContainSingle("the two rows were the same book all along");
        all.Values.Single().PersonalRating.Should().Be(5, "the more recent edit wins");
    }

    /// <summary>Running it again on an already-migrated database changes nothing.</summary>
    [Fact]
    public void TheMigrationIsANoOpTheSecondTime()
    {
        _store.Update("Book.epub", r => r.PersonalRating = 4);

        var reopened = new BookPersonalizationStore(new AppDatabase(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:Path"] = Path.Combine(_dir, "app.db") }).Build()));

        reopened.Get("book.epub")!.PersonalRating.Should().Be(4);
        reopened.LoadAll().Should().ContainSingle();
    }

    // ------------------------------------------------------ ClearGenreFields

    /// <summary>
    /// The admin "wipe genres" bulk operation. It must not take ratings and
    /// favourites with it — those are the fields nobody can reconstruct.
    /// </summary>
    [Fact]
    public void WipingGenresLeavesRatingsAndFavouritesIntact()
    {
        _store.Update("a.epub", r =>
        {
            r.PrimaryGenre = "Sci-Fi";
            r.Tags = ["Space"];
            r.PersonalRating = 5;
            r.FavoritedBy = ["Dad"];
            r.Series = "A Series";
        });

        _store.ClearGenreFields();

        var row = _store.Get("a.epub")!;
        row.PrimaryGenre.Should().BeNull();
        row.Tags.Should().BeNull();
        row.PersonalRating.Should().Be(5);
        row.FavoritedBy.Should().BeEquivalentTo("Dad");
        row.Series.Should().Be("A Series", "a series is not a genre");
    }

    [Fact]
    public void WipingGenresOnAnEmptyTableIsHarmless()
    {
        var act = () => _store.ClearGenreFields();

        act.Should().NotThrow();
    }

    // ------------------------------------------------------- the one-time import

    private string WriteSidecar(string fileName, LibraryBookMeta meta)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "library"));
        var path = Path.Combine(_dir, "library", fileName + ".meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta, LibraryHelpers.CreateLibraryJsonOptions()));
        return Path.Combine(_dir, "library");
    }

    private static LibraryBookMeta Meta(
        string fileName, string? genre = null, string[]? tags = null,
        int? rating = null, string? title = null, string[]? authors = null) =>
        new(title, authors, "epub", "1 MB", fileName, null, null, null, null,
            genre, tags, null, null, null, null, null, rating, null);

    /// <summary>Personalization made before this table existed has to carry over.</summary>
    [Fact]
    public void UserFieldsAreCarriedOverFromTheOldSidecars()
    {
        var root = WriteSidecar("a.epub", Meta("a.epub", genre: "Sci-Fi", rating: 5));

        _store.ImportFromMetaFilesIfNeeded(root);

        var row = _store.Get("a.epub")!;
        row.PrimaryGenre.Should().Be("Sci-Fi");
        row.PersonalRating.Should().Be(5);
    }

    /// <summary>
    /// Title and authors are deliberately not imported: in a sidecar they are
    /// indistinguishable from enrichment output, and importing them would freeze
    /// every future enrichment improvement behind a phantom "user override".
    /// </summary>
    [Fact]
    public void TitlesAndAuthorsAreDeliberatelyNotImported()
    {
        var root = WriteSidecar("a.epub",
            Meta("a.epub", genre: "Sci-Fi", title: "Enriched Title", authors: ["Enriched Author"]));

        _store.ImportFromMetaFilesIfNeeded(root);

        var row = _store.Get("a.epub")!;
        row.Title.Should().BeNull();
        row.Authors.Should().BeNull();
    }

    /// <summary>A sidecar with nothing a user chose is not worth a row.</summary>
    [Fact]
    public void ASidecarWithNoUserFieldsIsSkipped()
    {
        var root = WriteSidecar("a.epub", Meta("a.epub", title: "Just Enrichment"));

        _store.ImportFromMetaFilesIfNeeded(root);

        _store.LoadAll().Should().BeEmpty();
    }

    /// <summary>
    /// It runs once. Re-running would re-import sidecar values over edits made
    /// since, because the import upserts a whole fresh row rather than merging.
    /// </summary>
    [Fact]
    public void TheImportRunsOnceAndNotAgain()
    {
        var root = WriteSidecar("a.epub", Meta("a.epub", genre: "Sci-Fi"));
        _store.ImportFromMetaFilesIfNeeded(root);

        _store.Update("a.epub", r => r.PrimaryGenre = "History");
        _store.ImportFromMetaFilesIfNeeded(root);

        _store.Get("a.epub")!.PrimaryGenre.Should().Be("History",
            "the second call must be a no-op, or every edit since the import is reverted");
    }

    /// <summary>
    /// One malformed sidecar must not abort the import and strand every later
    /// book's personalization.
    /// </summary>
    [Fact]
    public void AMalformedSidecarDoesNotStopTheRest()
    {
        var root = WriteSidecar("good.epub", Meta("good.epub", genre: "Sci-Fi"));
        File.WriteAllText(Path.Combine(root, "bad.meta.json"), "{ not json");

        var act = () => _store.ImportFromMetaFilesIfNeeded(root);

        act.Should().NotThrow();
        _store.Get("good.epub").Should().NotBeNull();
    }

    /// <summary>A library that is not there yet still marks the import done.</summary>
    [Fact]
    public void AMissingLibraryFolderStillCompletesTheImport()
    {
        var act = () => _store.ImportFromMetaFilesIfNeeded(Path.Combine(_dir, "no-such-library"));

        act.Should().NotThrow();
        _db.GetState("book-personalization-imported").Should().NotBeNull(
            "otherwise it retries on every index rebuild forever");
    }
}
