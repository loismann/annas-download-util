using AnnasArchive.API.Services.Library;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// The enrichment ladder decides who a book is by asking four sources in order
/// and ranking their answers. That ranking was the untested part of the largest
/// method in the codebase: it never crashes when it gets it wrong, it just
/// files the book under someone else's name, and nobody notices until they go
/// looking for a book that is sitting in the library under a title from a
/// scanner's filename.
///
/// No network here — the four lookups are a seam, and what is asserted is
/// which of them get called and whose answer survives.
/// </summary>
public class BookEnrichmentPipelineTests
{
    private const string Title = "Pandora's Star";
    private const string Author = "Peter F. Hamilton";

    private readonly FakeLookups _lookups = new();
    private readonly Mock<IEnrichmentStatsService> _stats = new();
    private int _throttles;

    // ─── Rung 1: the catalogue ───────────────────────────────────────────

    [Fact]
    public async Task ATrustedCatalogueMatchOverwritesTheFilenameGuess()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9, title: "Pandora's Star", year: 2004);

        var meta = await Run(Seed(title: "pandoras_star_retail_v2"));

        meta["title"].Should().Be("Pandora's Star");
        meta["publishedDate"].Should().Be("2004");
        meta["openLibraryConfidence"].Should().Be(0.9);
    }

    [Fact]
    public async Task AnUnsureCatalogueMatchIsRecordedButNotBelieved()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.6, title: "Pandora Star", year: 1999);

        var meta = await Run(Seed());

        meta["title"].Should().Be(Title, "0.6 is not enough to rename somebody's book");
        meta["publishedDate"].Should().BeNull();
        meta["openLibraryConfidence"].Should().Be(0.6, "the number is still worth keeping");
    }

    [Fact]
    public async Task ASparseCatalogueRecordNeverBlanksAGoodLocalValue()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.95, title: "", authors: []);

        var meta = await Run(Seed());

        meta["title"].Should().Be(Title);
        meta["authors"].Should().BeEquivalentTo(new[] { Author });
    }

    [Fact]
    public async Task AsksNobodyAboutABookWithNoUsableTitle()
    {
        // "IsMetadataReliable" is the gate: a two-character title is not a
        // search, it is a way to get a confident answer about the wrong book.
        var meta = await Run(Seed(title: "x"));

        _lookups.OpenLibraryCalls.Should().BeEmpty();
        _lookups.AiCalls.Should().BeEmpty();
        meta["openLibraryConfidence"].Should().BeNull();
    }

    // ─── Rung 2: the model's verdict ─────────────────────────────────────

    [Fact]
    public async Task DoesNotPayForAVerdictOnAConfidentMatch()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9);

        await Run(Seed());

        _lookups.AiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task NeverReAsksTheModelAboutABookItAlreadyRuledOn()
    {
        // The verdict was written into the file. Asking again buys the same
        // answer at the same price.
        _lookups.OpenLibrary = Catalogue(confidence: 0.2);
        var seed = Seed();
        seed["aiEnrichedAt"] = "2026-01-01T00:00:00Z";

        await Run(seed);

        _lookups.AiCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PromotesTheCatalogueWhenTheModelVouchesForIt()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.4, title: "Pandora's Star", year: 2004);
        _lookups.Ai = new AiValidationAndEnrichment(
            UseOpenLibrary: true, Title: null, Authors: [], PublishedDate: null, Series: null, CoverUrl: null);

        var meta = await Run(Seed(title: "pandoras star"));

        meta["title"].Should().Be("Pandora's Star");
        meta["openLibraryConfidence"].Should().Be(BookEnrichmentPipeline.TrustedConfidence,
            "the doubt about the match is exactly what the model resolved");
        meta["publishedDate"].Should().BeNull(
            "the model vouched for who wrote it, not for a publication year it never saw");
        meta["aiEnrichedAt"].Should().NotBeNull();
    }

    [Fact]
    public async Task DoesNotRetryTheCatalogueAfterTheModelVouchedForIt()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.4);
        _lookups.Ai = new AiValidationAndEnrichment(true, null, [], null, null, null);

        await Run(Seed());

        _lookups.OpenLibraryCalls.Should().ContainSingle("a second identical lookup returns the same thing");
    }

    // ─── Rung 2b: the retry that justifies the whole ladder ──────────────

    [Fact]
    public async Task AsksTheCatalogueAgainWithTheTitleTheModelCorrected()
    {
        // This is the point of the ladder. "hp3_pa_scan_v2" finds nothing;
        // "Harry Potter and the Prisoner of Azkaban" finds it immediately.
        _lookups.OpenLibrary = Catalogue(confidence: 0.1);
        _lookups.Ai = new AiValidationAndEnrichment(
            UseOpenLibrary: false,
            Title: "Harry Potter and the Prisoner of Azkaban",
            Authors: ["J.K. Rowling"],
            PublishedDate: "1999", Series: "Harry Potter", CoverUrl: null);
        _lookups.OpenLibraryRetry = Catalogue(confidence: 0.95,
            title: "Harry Potter and the Prisoner of Azkaban", year: 1999);

        var meta = await Run(Seed(title: "hp3_pa_scan_v2"));

        _lookups.OpenLibraryCalls.Should().HaveCount(2);
        _lookups.OpenLibraryCalls[1].Title.Should().Be("Harry Potter and the Prisoner of Azkaban");
        meta["openLibraryConfidence"].Should().Be(0.95);
        meta["title"].Should().Be("Harry Potter and the Prisoner of Azkaban");
    }

    [Fact]
    public async Task KeepsTheModelsAnswerWhenTheRetryIsStillUnsure()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.1);
        _lookups.Ai = new AiValidationAndEnrichment(
            false, "The Real Title", ["Real Author"], "2001", null, null);
        _lookups.OpenLibraryRetry = Catalogue(confidence: 0.5, title: "Something Else");

        var meta = await Run(Seed(title: "junk_scan_01"));

        meta["title"].Should().Be("The Real Title", "a second vague match is not an improvement");
        meta["authors"].Should().BeEquivalentTo(new[] { "Real Author" });
    }

    [Fact]
    public async Task SurvivesAModelAnswerWithNoAuthors()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.1);
        _lookups.Ai = new AiValidationAndEnrichment(false, "A Title", null!, null, null, null);

        var meta = await Run(Seed(title: "junk_scan_01"));

        meta["authors"].Should().BeEquivalentTo(Array.Empty<string>());
        _lookups.OpenLibraryCalls.Should().HaveCount(2);
    }

    // ─── Covers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NeverReplacesTheCoverExtractedFromTheBookItself()
    {
        // A _covers/ file came out of this exact EPUB. Every remote thumbnail
        // is a guess about an edition; this one is the edition.
        _lookups.OpenLibrary = Catalogue(confidence: 0.9, cover: "https://covers.openlibrary.org/x.jpg");
        _lookups.GoogleBooksCover = "https://books.google.com/y.jpg";

        var seed = Seed();
        seed["coverUrl"] = "_covers/pandoras-star.jpg";

        var meta = await Run(seed);

        meta["coverUrl"].Should().Be("_covers/pandoras-star.jpg");
        _lookups.GoogleBooksCalls.Should().BeEmpty("there is already a cover, so there is nothing to ask for");
    }

    [Fact]
    public async Task TakesACatalogueCoverWhenThereIsNone()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9, cover: "https://covers.openlibrary.org/x.jpg");
        _lookups.GoogleBooksCover = "https://books.google.com/y.jpg";

        var meta = await Run(Seed());

        meta["coverUrl"].Should().Be("https://covers.openlibrary.org/x.jpg");
        _lookups.GoogleBooksCalls.Should().BeEmpty("the first source that answered wins");
    }

    [Fact]
    public async Task FallsBackToGoogleBooksForACoverAndNothingElse()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9, cover: null);
        _lookups.GoogleBooksCover = "https://books.google.com/y.jpg";

        var meta = await Run(Seed());

        _lookups.GoogleBooksCalls.Should().ContainSingle();
        meta["coverUrl"].Should().Be("https://books.google.com/y.jpg");
    }

    // ─── Ratings ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchesARatingWhenThereIsNone()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9);
        _lookups.GoodreadsRating = 4.21;

        var meta = await Run(Seed());

        meta["goodreadsRating"].Should().Be(4.21);
    }

    [Fact]
    public async Task DoesNotRefetchARatingItAlreadyHas()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9);
        var seed = Seed();
        seed["goodreadsRating"] = 4.21;

        var meta = await Run(seed);

        _lookups.GoodreadsCalls.Should().BeEmpty();
        meta["goodreadsRating"].Should().Be(4.21);
    }

    [Fact]
    public async Task LeavesTheRatingNullWhenGoodreadsHasNothing()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.9);
        _lookups.GoodreadsRating = null;

        var meta = await Run(Seed());

        _lookups.GoodreadsCalls.Should().ContainSingle();
        meta["goodreadsRating"].Should().BeNull();
    }

    // ─── Throttling and accounting ───────────────────────────────────────

    [Fact]
    public async Task PausesBetweenSourcesButNotAfterTheLastOne()
    {
        // Four rungs run; the pause after the final one only delays the next
        // book, which is the one thing it cannot protect.
        _lookups.OpenLibrary = Catalogue(confidence: 0.1);
        _lookups.Ai = new AiValidationAndEnrichment(false, "Corrected", ["A"], null, null, null);
        _lookups.OpenLibraryRetry = Catalogue(confidence: 0.9);

        await Run(Seed(title: "junk_scan_01"));

        _throttles.Should().Be(4,
            "OpenLibrary, the model, the retry and Google Books all pause — Goodreads is last and does not");
    }

    [Fact]
    public async Task DoesNotPauseForARungItSkipped()
    {
        // Only OpenLibrary runs: it is confident, so no model call and no
        // retry; a cover is already present, so no Google Books.
        _lookups.OpenLibrary = Catalogue(confidence: 0.9);
        var seed = Seed();
        seed["coverUrl"] = "_covers/local.jpg";
        seed["goodreadsRating"] = 4.0;

        await Run(seed);

        _throttles.Should().Be(1);
    }

    [Fact]
    public async Task RecordsEachSourceSeparately()
    {
        _lookups.OpenLibrary = Catalogue(confidence: 0.1);
        _lookups.Ai = new AiValidationAndEnrichment(false, "Corrected", ["A"], null, null, null);
        _lookups.OpenLibraryRetry = Catalogue(confidence: 0.9);
        _lookups.GoodreadsRating = 4.0;

        await Run(Seed(title: "junk_scan_01"));

        _stats.Verify(s => s.RecordCall("OpenLibrary", false, 0.1), Times.Once,
            "0.1 is a miss even though a record came back");
        _stats.Verify(s => s.RecordCall("GPT4", true, null), Times.Once);
        _stats.Verify(s => s.RecordCall("OpenLibrary_Retry", true, 0.9), Times.Once);
        _stats.Verify(s => s.RecordCall("Goodreads", true, null), Times.Once);
    }

    [Fact]
    public async Task CountsAPlausibleButUntrustedMatchAsASuccess()
    {
        // 0.5 found the book; 0.75 is the separate question of whether to
        // overwrite what we already had. Conflating them would hide a source
        // that is working from a source that is failing.
        _lookups.OpenLibrary = Catalogue(confidence: 0.6);

        await Run(Seed());

        _stats.Verify(s => s.RecordCall("OpenLibrary", true, 0.6), Times.Once);
    }

    [Fact]
    public async Task SurvivesEverySourceReturningNothing()
    {
        var meta = await Run(Seed());

        meta["title"].Should().Be(Title);
        meta["coverUrl"].Should().BeNull();
        meta["goodreadsRating"].Should().BeNull();
    }

    // ─── Cover rule, directly ────────────────────────────────────────────

    [Theory]
    [InlineData(null, "https://remote/x.jpg", true)]
    [InlineData("", "https://remote/x.jpg", true)]
    [InlineData("   ", "https://remote/x.jpg", true)]
    [InlineData("_covers/local.jpg", "https://remote/x.jpg", false)]
    [InlineData("https://old/x.jpg", "https://remote/x.jpg", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "", false)]
    public void ReplacesACoverOnlyWhenThereIsNoneAndTheCandidateIsReal(
        string? current, string? candidate, bool expected)
    {
        BookEnrichmentPipeline.ShouldReplaceCover(current, candidate).Should().Be(expected);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Seed(string title = Title, string author = Author) =>
        new()
        {
            ["title"] = title,
            ["authors"] = new[] { author },
            ["coverUrl"] = null,
            ["series"] = null,
            ["publishedDate"] = null,
            ["goodreadsRating"] = null,
            ["aiEnrichedAt"] = null,
            ["openLibraryConfidence"] = null
        };

    private static OpenLibraryData Catalogue(
        double confidence,
        string? title = Title,
        string[]? authors = null,
        int? year = null,
        string? cover = null,
        string? series = null) =>
        new(cover, null, [], series, title, authors ?? [Author], year, confidence, []);

    private async Task<Dictionary<string, object?>> Run(Dictionary<string, object?> meta)
    {
        var pipeline = new BookEnrichmentPipeline(_lookups, _stats.Object, _ =>
        {
            _throttles++;
            return Task.CompletedTask;
        });

        await pipeline.RunAsync(meta, "pandoras star.epub", CancellationToken.None);
        return meta;
    }

    private sealed class FakeLookups : IBookMetadataLookups
    {
        public OpenLibraryData? OpenLibrary { get; set; }
        public OpenLibraryData? OpenLibraryRetry { get; set; }
        public AiValidationAndEnrichment? Ai { get; set; }
        public string? GoogleBooksCover { get; set; }
        public double? GoodreadsRating { get; set; }

        public List<(string Title, string[] Authors)> OpenLibraryCalls { get; } = [];
        public List<(string Title, string FileName)> AiCalls { get; } = [];
        public List<string> GoogleBooksCalls { get; } = [];
        public List<string> GoodreadsCalls { get; } = [];

        public Task<OpenLibraryData?> OpenLibraryAsync(string title, string[] authors, CancellationToken token)
        {
            OpenLibraryCalls.Add((title, authors));
            return Task.FromResult(OpenLibraryCalls.Count == 1 ? OpenLibrary : OpenLibraryRetry);
        }

        public Task<AiValidationAndEnrichment?> ValidateWithAiAsync(
            string title, string[] authors, string fileName, OpenLibraryData? openLibrary, CancellationToken token)
        {
            AiCalls.Add((title, fileName));
            return Task.FromResult(Ai);
        }

        public Task<string?> GoogleBooksCoverAsync(string title, string[] authors, CancellationToken token)
        {
            GoogleBooksCalls.Add(title);
            return Task.FromResult(GoogleBooksCover);
        }

        public Task<double?> GoodreadsRatingAsync(string title, string[] authors, CancellationToken token)
        {
            GoodreadsCalls.Add(title);
            return Task.FromResult(GoodreadsRating);
        }
    }
}
