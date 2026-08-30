using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// The daily library-review prompt: which books it offers, in what order, and what
/// it refuses to offer at all.
///
/// <para>293 lines with no test naming it, and one of its decisions is
/// <b>delete the file</b>. The rule that keeps that safe is that only books tagged
/// <i>exclusively</i> as Paul's ever enter the flow — a keep/delete here is made
/// unilaterally, so a book Mom or Dad also claims must never appear. That rule is
/// one predicate, and nothing was checking it.</para>
/// </summary>
public sealed class LibraryReviewServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "libreview-tests", Guid.NewGuid().ToString("N"));

    private readonly LibraryIndexCache _cache;
    private readonly BookPersonalizationStore _personalization;
    private readonly LibraryReviewService _svc;

    public LibraryReviewServiceTests()
    {
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "app.db")
            })
            .Build();

        var db = new AppDatabase(config);
        _personalization = new BookPersonalizationStore(db);
        _cache = new LibraryIndexCache();
        _cache.GetBooks("");   // force the (empty) build so incremental adds land

        _svc = new LibraryReviewService(
            _cache, db, _personalization, Path.Combine(_dir, "no-legacy-file.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private const string PaulTag = "Paul's Books";
    private const string MomTag = "Mom's Books";
    private const string DadTag = "Dad's Books";

    private LibraryBookDto Add(
        string title,
        string[]? tags = null,
        string? genre = "Sci-Fi",
        DateTime? cullReviewedAt = null)
    {
        var book = new LibraryBookDto(
            title, ["An Author"], "epub", "1 MB", title + ".epub",
            "https://covers.test/x.jpg", "test", null, new DateTime(2026, 1, 1),
            genre, tags ?? [PaulTag], null, [], null, null, null, null, [], cullReviewedAt);

        _cache.UpdateBook(book);
        return book;
    }

    // --------------------------------------------------- who may be reviewed

    /// <summary>
    /// The safety rule. A keep/delete decision in this flow is made by one person,
    /// so a book someone else also claims must never be offered — a plain "contains
    /// Paul's tag" check would include it and hand Paul a delete button for Mom's
    /// book.
    /// </summary>
    [Theory]
    [InlineData("Paul's Books,Mom's Books")]
    [InlineData("Paul's Books,Dad's Books")]
    [InlineData("Paul's Books,Mom's Books,Dad's Books")]
    public void ABookSharedWithSomeoneElseIsNeverOffered(string tagList)
    {
        Add("Shared", tags: tagList.Split(','), cullReviewedAt: null);

        _svc.GetStatus("").Phase.Should().Be("complete",
            "a shared book is not Paul's alone, so there is nothing for him to decide");
    }

    [Fact]
    public void ABookNobodyOwnsIsNotOffered()
    {
        Add("Unowned", tags: []);

        _svc.GetStatus("").Phase.Should().Be("complete");
    }

    [Fact]
    public void ABookOnlyMomOwnsIsNotOffered()
    {
        Add("Moms", tags: [MomTag]);

        _svc.GetStatus("").Phase.Should().Be("complete");
    }

    [Fact]
    public void ABookOnlyPaulOwnsIsOffered()
    {
        Add("Pauls", tags: [PaulTag]);

        var status = _svc.GetStatus("");
        status.Phase.Should().Be("cull");
        status.RemainingInPhase.Should().Be(1);
    }

    /// <summary>Owner tags come from a picker whose casing nobody controls.</summary>
    [Fact]
    public void OwnerTagsAreMatchedWithoutRegardToCase()
    {
        Add("Pauls", tags: ["paul's books", "mom's books"]);

        _svc.GetStatus("").Phase.Should().Be("complete",
            "the shared tag still counts even in a different case");
    }

    // ------------------------------------------------------------- phases

    /// <summary>
    /// Cull comes first and genre only starts once nothing is left to cull —
    /// deciding a book's genre before deciding whether to keep it is wasted effort.
    /// </summary>
    [Fact]
    public void CullComesBeforeGenre()
    {
        Add("NeedsCull", genre: null, cullReviewedAt: null);

        _svc.GetStatus("").Phase.Should().Be("cull");
    }

    [Fact]
    public void GenreStartsOnceNothingIsLeftToCull()
    {
        Add("Culled", genre: null, cullReviewedAt: DateTime.UtcNow);

        var status = _svc.GetStatus("");
        status.Phase.Should().Be("genre");
        status.RemainingInPhase.Should().Be(1);
    }

    [Fact]
    public void AFullyReviewedLibraryIsComplete()
    {
        Add("Done", genre: "Sci-Fi", cullReviewedAt: DateTime.UtcNow);

        _svc.GetStatus("").Phase.Should().Be("complete");
    }

    /// <summary>
    /// "Uncategorized" is what enrichment writes when it cannot tell, so it means
    /// the same thing as blank. Treating it as a real genre would silently skip
    /// every book that most needs a human.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Uncategorized")]
    [InlineData("uncategorized")]
    public void AnUnsetGenreIncludesTheWordUncategorized(string? genre)
    {
        Add("NoGenre", genre: genre, cullReviewedAt: DateTime.UtcNow);

        _svc.GetStatus("").Phase.Should().Be("genre");
    }

    /// <summary>
    /// The phase is recomputed from current eligibility every time rather than
    /// latched. An earlier version set a permanent "done" flag the first time a
    /// phase hit zero, which silently stopped the prompt forever — including for
    /// books added afterwards, since the latch never re-checked.
    /// </summary>
    [Fact]
    public void ABookAddedAfterAPassIsPickedUpRatherThanLatchedOut()
    {
        Add("Done", cullReviewedAt: DateTime.UtcNow);
        _svc.GetStatus("").Phase.Should().Be("complete");

        Add("BrandNew", cullReviewedAt: null);

        _svc.GetStatus("").Phase.Should().Be("cull",
            "eligibility is live, so a new book reopens the phase");
    }

    // ------------------------------------------------------------ sessions

    [Fact]
    public void ASessionOffersTheEligibleBooks()
    {
        Add("A");
        Add("B");

        var session = _svc.StartOrResumeSession("");

        session.Phase.Should().Be("cull");
        session.Books.Select(b => b.FileName).Should().BeEquivalentTo("A.epub", "B.epub");
        session.TotalRemainingInPhase.Should().Be(2);
    }

    /// <summary>
    /// A batch is capped so the prompt stays a few minutes' work, but the remaining
    /// count still describes the whole job — otherwise the end never appears to get
    /// closer.
    /// </summary>
    [Fact]
    public void ABatchIsCappedButTheRemainingCountIsNot()
    {
        for (var i = 0; i < 25; i++) Add($"Book{i:00}");

        var session = _svc.StartOrResumeSession("");

        session.Books.Should().HaveCount(20);
        session.TotalRemainingInPhase.Should().Be(25);
    }

    /// <summary>
    /// Triggering the exercise again the same day must advance to fresh books.
    /// A previous version never reset the exhausted batch, so a second trigger
    /// returned zero undecided books forever — looking "all done" with thousands
    /// still untouched.
    /// </summary>
    [Fact]
    public async Task TriggeringAgainAfterFinishingABatchDrawsMoreRatherThanLookingDone()
    {
        for (var i = 0; i < 25; i++) Add($"Book{i:00}");

        var first = _svc.StartOrResumeSession("");
        var decided = first.Books.Select(b => b.FileName).ToHashSet();
        foreach (var b in first.Books)
            await _svc.RecordDecisionAsync(b.FileName, "keep");

        // No re-seeding: the index now carries each decision itself. This used to
        // need a hand-rolled rebuild here, because every "keep" dropped the whole
        // index and the twenty just recorded vanished with it.
        var second = _svc.StartOrResumeSession("");

        second.Books.Should().HaveCount(5, "twenty of twenty-five were decided");
        second.Books.Select(b => b.FileName).Should().NotIntersectWith(decided,
            "a book decided earlier today must not come round again");
    }

    /// <summary>
    /// A "keep" edits its own row and nothing else. It used to invalidate the whole
    /// index, so a twenty-book batch forced twenty full rebuilds — and because
    /// <c>MetaIndexCache</c> answers with an empty list while a rebuild is in
    /// flight, one person working through a review session could make the library
    /// page repeatedly report zero books to everyone else.
    /// </summary>
    [Fact]
    public async Task KeepingABookLeavesTheRestOfTheIndexStanding()
    {
        Add("Kept");
        Add("Untouched");

        await _svc.RecordDecisionAsync("Kept.epub", "keep");

        _cache.IsCached.Should().BeTrue("the index was patched, not dropped");
        var books = _cache.GetBooks("");
        books.Should().HaveCount(2);
        books.Single(b => b.FileName == "Kept.epub").CullReviewedAt.Should().NotBeNull();
        books.Single(b => b.FileName == "Untouched.epub").CullReviewedAt.Should().BeNull();
    }

    /// <summary>
    /// The patch has to reach the index, not just the store — the review pool is
    /// computed from the index, so a kept book whose row never changed would be
    /// offered again on the next draw.
    /// </summary>
    [Fact]
    public async Task AKeptBookLeavesTheReviewPoolWithoutARebuild()
    {
        Add("Kept");
        Add("Waiting");

        await _svc.RecordDecisionAsync("Kept.epub", "keep");

        _svc.GetStatus("").RemainingInPhase.Should().Be(1);
    }

    /// <summary>
    /// The safety valve. If the index does not hold the book, the row cannot be
    /// patched, and leaving it alone would let the index and the store disagree
    /// forever — the store says reviewed, the index keeps offering it. A rebuild
    /// re-reads the store, so falling back costs one rebuild and stays honest.
    /// </summary>
    [Fact]
    public async Task AKeepForABookTheIndexDoesNotHoldFallsBackToARebuild()
    {
        Add("Present");
        _cache.IsCached.Should().BeTrue();

        var result = await _svc.RecordDecisionAsync("Absent.epub", "keep");

        result.Success.Should().BeTrue();
        _cache.IsCached.Should().BeFalse(
            "an unpatchable keep must not leave the index contradicting the store");
    }

    /// <summary>
    /// The patch must edit the <i>stored</i> row, whose cover URL is relative. An
    /// implementation that read a book back out of <c>GetBooks(baseUrl)</c> and
    /// wrote it back would bake one request's hostname into the shared cache, and
    /// <c>NormalizeUrls</c> passes anything already absolute straight through — so
    /// every other caller would then be served that host until the next rebuild.
    /// </summary>
    [Fact]
    public async Task KeepingABookDoesNotBakeARequestHostIntoTheIndex()
    {
        _cache.UpdateBook(new LibraryBookDto(
            "Relative", ["An Author"], "epub", "1 MB", "Relative.epub",
            "/api/library/cover/Relative.jpg", "test", null, new DateTime(2026, 1, 1),
            "Sci-Fi", [PaulTag], null, [], null, null, null, null, [], null));

        _cache.GetBooks("https://first.example");
        await _svc.RecordDecisionAsync("Relative.epub", "keep");

        _cache.GetBooks("https://second.example").Single().CoverUrl
            .Should().Be("https://second.example/api/library/cover/Relative.jpg");
    }

    /// <summary>
    /// Resuming returns the same batch, so closing the tab mid-way does not
    /// reshuffle what is on screen.
    /// </summary>
    [Fact]
    public void ResumingReturnsTheSameBatch()
    {
        for (var i = 0; i < 25; i++) Add($"Book{i:00}");

        var first = _svc.StartOrResumeSession("").Books.Select(b => b.FileName).ToList();
        var resumed = _svc.StartOrResumeSession("").Books.Select(b => b.FileName).ToList();

        resumed.Should().BeEquivalentTo(first);
    }

    /// <summary>
    /// A book that stopped being eligible while the batch was open — deleted
    /// elsewhere, or its genre fixed in the normal editor — is dropped rather than
    /// leaving a session that can never be finished.
    /// </summary>
    [Fact]
    public void ABookThatFellOutOfEligibilityDoesNotStrandTheSession()
    {
        Add("Stays");
        Add("Leaves");

        _svc.StartOrResumeSession("").Books.Should().HaveCount(2);

        _cache.RemoveBook("Leaves.epub");

        _svc.StartOrResumeSession("").Books
            .Select(b => b.FileName).Should().BeEquivalentTo("Stays.epub");
    }

    /// <summary>A session on a finished library is empty rather than an error.</summary>
    [Fact]
    public void AFinishedLibraryReturnsAnEmptyCompleteSession()
    {
        Add("Done", cullReviewedAt: DateTime.UtcNow);

        var session = _svc.StartOrResumeSession("");

        session.Phase.Should().Be("complete");
        session.Books.Should().BeEmpty();
        session.TotalRemainingInPhase.Should().Be(0);
    }

    // ----------------------------------------------------------- decisions

    /// <summary>
    /// The file name reaches a filesystem path, so anything that is not a bare name
    /// is refused rather than sanitised — the caller should not get a success for a
    /// book it did not name.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/dir/book.epub")]
    [InlineData("/absolute/book.epub")]
    public async Task ADecisionOnAPathRatherThanAFileNameIsRefused(string fileName)
    {
        var result = await _svc.RecordDecisionAsync(fileName, "keep");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid fileName");
    }

    [Fact]
    public async Task AnUnknownDecisionIsRefused()
    {
        Add("A");

        var result = await _svc.RecordDecisionAsync("A.epub", "maybe");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown decision");
    }

    /// <summary>
    /// "Keep" writes to the personalization store, never the enrichment sidecar —
    /// that separation is what stops re-enrichment from resurfacing a book already
    /// reviewed.
    /// </summary>
    [Fact]
    public async Task KeepingABookRecordsTheReviewInThePersonalizationStore()
    {
        Add("A");

        var result = await _svc.RecordDecisionAsync("A.epub", "keep");

        result.Success.Should().BeTrue();
        _personalization.Get("A.epub")!.CullReviewedAt.Should().NotBeNull();
    }

    /// <summary>
    /// The genre phase cannot be completed by clicking through it — the decision is
    /// only accepted once a genre actually exists, so "done" always means done.
    /// </summary>
    [Fact]
    public async Task ConfirmingAGenreThatWasNeverSetIsRefused()
    {
        Add("A", genre: null, cullReviewedAt: DateTime.UtcNow);

        var result = await _svc.RecordDecisionAsync("A.epub", "genreSet");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("has not been set");
    }

    [Fact]
    public async Task ConfirmingAGenreThatHasBeenSetSucceeds()
    {
        Add("A", genre: null, cullReviewedAt: DateTime.UtcNow);
        _personalization.Update("A.epub", p => p.PrimaryGenre = "History");

        var result = await _svc.RecordDecisionAsync("A.epub", "genreSet");

        result.Success.Should().BeTrue();
    }

    /// <summary>
    /// A genre explicitly cleared by the user is still missing — the override's
    /// empty string must not read as a genre named "".
    /// </summary>
    [Fact]
    public async Task AGenreClearedByTheUserStillCountsAsUnset()
    {
        Add("A", genre: null, cullReviewedAt: DateTime.UtcNow);
        _personalization.Update("A.epub", p => p.PrimaryGenre = "");

        var result = await _svc.RecordDecisionAsync("A.epub", "genreSet");

        result.Success.Should().BeFalse();
    }

    // -------------------------------------------------------- the daily gate

    /// <summary>
    /// The prompt is once a day. Showing it again the same session would make it
    /// nagging rather than a habit — and the flag is set by opening a session, not
    /// by finishing one.
    /// </summary>
    [Fact]
    public void OpeningASessionSuppressesThePromptForTheRestOfTheDay()
    {
        Add("A");
        _svc.GetStatus("").ShouldShow.Should().BeTrue("nothing has been shown yet today");

        _svc.StartOrResumeSession("");

        _svc.GetStatus("").ShouldShow.Should().BeFalse();
    }

    /// <summary>
    /// But an unfinished batch is still reported as in progress, so the UI can
    /// offer to resume it without the prompt reappearing on its own.
    /// </summary>
    [Fact]
    public void AnUnfinishedBatchIsStillReportedAsInProgress()
    {
        Add("A");
        Add("B");

        _svc.StartOrResumeSession("");

        _svc.GetStatus("").SessionInProgress.Should().BeTrue();
    }

    [Fact]
    public async Task AFullyDecidedBatchIsNoLongerInProgress()
    {
        Add("A");

        var session = _svc.StartOrResumeSession("");
        foreach (var b in session.Books)
            await _svc.RecordDecisionAsync(b.FileName, "keep");

        _svc.GetStatus("").SessionInProgress.Should().BeFalse();
    }
}
