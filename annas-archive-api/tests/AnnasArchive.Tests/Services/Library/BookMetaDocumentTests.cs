using AnnasArchive.API.Services.Library;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// Every enrichment pass rewrites the whole <c>.meta.json</c>, so a field this
/// seed forgets to carry forward is a field the background scanner deletes.
/// The dangerous ones are the fields a person typed — nothing in the enrichment
/// ladder ever writes a favourite or a personal rating, so nothing would ever
/// put one back.
/// </summary>
public class BookMetaDocumentTests
{
    private const string Path = "/library/Pandoras Star - Peter F. Hamilton.epub";

    [Fact]
    public void CarriesForwardEverythingAPersonTyped()
    {
        var existing = new ExistingMeta
        {
            Title = "Pandora's Star",
            Authors = ["Peter F. Hamilton"],
            Tags = ["Paul's Books", "space opera"],
            PersonalRating = 5,
            ReaderEnabled = true,
            FavoritedBy = ["Paul", "Mom"],
            CullReviewedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        var meta = BookMetaDocument.Seed(existing, "Pandoras Star", ["Peter F. Hamilton"], Path, 1024);

        meta["tags"].Should().BeEquivalentTo(new[] { "Paul's Books", "space opera" });
        meta["personalRating"].Should().Be(5);
        meta["readerEnabled"].Should().Be(true);
        meta["favoritedBy"].Should().BeEquivalentTo(new[] { "Paul", "Mom" });
        meta["cullReviewedAt"].Should().Be("2026-03-01T12:00:00.0000000Z");
    }

    [Fact]
    public void CarriesForwardWhatEarlierPassesFound()
    {
        // A pass that cannot reach OpenLibrary must not erase what the last one
        // learned there.
        var existing = new ExistingMeta
        {
            Title = "Pandora's Star",
            Authors = ["Peter F. Hamilton"],
            CoverUrl = "_covers/pandoras-star.jpg",
            Series = "Commonwealth Saga",
            PublishedDate = "2004",
            Pages = "1144",
            Description = "First contact goes badly.",
            PrimaryGenre = "Science Fiction",
            Genres = ["Science Fiction", "Space Opera"],
            GoodreadsRating = 4.21,
            OpenLibraryConfidence = 0.93,
            AiEnrichedAt = "2026-01-01T00:00:00Z"
        };

        var meta = BookMetaDocument.Seed(existing, null, null, Path, 1024);

        meta["coverUrl"].Should().Be("_covers/pandoras-star.jpg");
        meta["series"].Should().Be("Commonwealth Saga");
        meta["publishedDate"].Should().Be("2004");
        meta["pages"].Should().Be("1144");
        meta["description"].Should().Be("First contact goes badly.");
        meta["genres"].Should().BeEquivalentTo(new[] { "Science Fiction", "Space Opera" });
        meta["goodreadsRating"].Should().Be(4.21);
        meta["openLibraryConfidence"].Should().Be(0.93);
        meta["aiEnrichedAt"].Should().Be("2026-01-01T00:00:00Z");
    }

    [Fact]
    public void KeepsProvenanceFromWhenTheBookArrived()
    {
        var existing = new ExistingMeta
        {
            Source = "annas-archive",
            Md5 = "d41d8cd98f00b204e9800998ecf8427e",
            SavedAt = "2025-06-01T00:00:00Z"
        };

        var meta = BookMetaDocument.Seed(existing, "Pandoras Star", [], Path, 1024);

        meta["source"].Should().Be("annas-archive");
        meta["md5"].Should().Be("d41d8cd98f00b204e9800998ecf8427e");
        meta["savedAt"].Should().Be("2025-06-01T00:00:00Z", "the arrival time does not change on a rescan");
    }

    [Fact]
    public void RederivesTheFileFactsEveryPass()
    {
        // The file is the authority on itself — a stale size after a re-download
        // would be wrong in a way nothing else corrects.
        var existing = new ExistingMeta { Title = "Pandora's Star" };

        var meta = BookMetaDocument.Seed(existing, null, null, Path, 5_242_880);

        meta["format"].Should().Be("EPUB");
        meta["fileName"].Should().Be("Pandoras Star - Peter F. Hamilton.epub");
        meta["fileSize"].Should().Be("5.0MB");
    }

    [Fact]
    public void PrefersTheFilenameOverATitleNobodyChose()
    {
        // The stored title is the raw base name, which means nothing has ever
        // improved on it — the parsed one is a real guess.
        var path = "/library/pandoras_star_retail.epub";
        var existing = new ExistingMeta { Title = "pandoras_star_retail" };

        var meta = BookMetaDocument.Seed(existing, "Pandoras Star", ["Peter F. Hamilton"], path, 1024);

        meta["title"].Should().Be("Pandoras Star");
    }

    [Fact]
    public void KeepsAStoredTitleThatSomeoneOrSomethingImproved()
    {
        var existing = new ExistingMeta { Title = "Pandora's Star" };

        var meta = BookMetaDocument.Seed(existing, "Pandoras Star", [], Path, 1024);

        meta["title"].Should().Be("Pandora's Star");
    }

    [Fact]
    public void PrefersStoredAuthorsOverAFilenameGuess()
    {
        var existing = new ExistingMeta { Authors = ["Peter F. Hamilton"] };

        var meta = BookMetaDocument.Seed(existing, null, ["P F Hamilton"], Path, 1024);

        meta["authors"].Should().BeEquivalentTo(new[] { "Peter F. Hamilton" });
    }

    [Fact]
    public void FallsBackToTheFilenameAuthorsWhenNoneAreStored()
    {
        var existing = new ExistingMeta { Authors = [] };

        var meta = BookMetaDocument.Seed(existing, null, ["Peter F. Hamilton"], Path, 1024);

        meta["authors"].Should().BeEquivalentTo(new[] { "Peter F. Hamilton" });
    }

    [Fact]
    public void FallsBackToTheBaseNameWhenNothingHasATitle()
    {
        // Never null: this value is what the library page shows, and a book with
        // no name at all cannot be found again by the person looking for it.
        var meta = BookMetaDocument.Seed(null, null, null, Path, 1024);

        meta["title"].Should().Be("Pandoras Star - Peter F. Hamilton");
        meta["authors"].Should().BeEquivalentTo(Array.Empty<string>());
    }

    [Fact]
    public void StartsABrandNewBookAsUnenriched()
    {
        var meta = BookMetaDocument.Seed(null, "Pandoras Star", ["Peter F. Hamilton"], Path, 1024);

        meta["enrichmentComplete"].Should().Be(false);
        meta["source"].Should().Be("library");
        meta["savedAt"].Should().NotBeNull();
        meta["favoritedBy"].Should().BeEquivalentTo(Array.Empty<string>());
        meta["tags"].Should().BeEquivalentTo(Array.Empty<string>());
    }

    [Fact]
    public void CarriesForwardThatAPassAlreadyFinished()
    {
        var existing = new ExistingMeta { Title = "Pandora's Star", EnrichmentComplete = true };

        BookMetaDocument.Seed(existing, null, null, Path, 1024)["enrichmentComplete"].Should().Be(true);
    }
}
