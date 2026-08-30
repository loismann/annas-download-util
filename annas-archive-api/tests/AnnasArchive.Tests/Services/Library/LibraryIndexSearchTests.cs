using AnnasArchive.API.Models;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// The library's server-side search: every filter, and the sorts that have their
/// own hidden filter attached.
///
/// <para>440 lines with no test naming it, and it is the only thing standing
/// between the library page and the whole collection — the frontend never loads
/// all the books, so whatever this returns <i>is</i> the library as far as anyone
/// can tell. A filter that is subtly wrong does not throw; it just quietly hides
/// books, which is indistinguishable from not owning them.</para>
///
/// <para>Seeded through <c>UpdateBook</c> rather than from disk: the cache builds
/// empty because <c>LIBRARY_ROOT</c> is not set under the test binary, and an
/// empty build is still a build, so incremental adds land in a real cache. No
/// environment variables are touched, so these stay safe to run in parallel.</para>
/// </summary>
public class LibraryIndexSearchTests
{
    private static LibraryBookDto Book(
        string title,
        string? fileName = null,
        string[]? authors = null,
        string? primaryGenre = null,
        string[]? tags = null,
        string? series = null,
        double? goodreads = null,
        int? personalRating = null,
        string[]? favoritedBy = null,
        string? coverUrl = "https://covers.test/x.jpg",
        DateTime? savedAt = null) =>
        new(title, authors ?? ["An Author"], "epub", "1 MB", fileName ?? title + ".epub",
            coverUrl, "test", null, savedAt ?? new DateTime(2026, 1, 1),
            primaryGenre, tags ?? [], series, [], null, null,
            goodreads, personalRating, favoritedBy ?? [], null);

    /// <summary>A cache holding exactly these books and nothing else.</summary>
    private static LibraryIndexCache CacheOf(params LibraryBookDto[] books)
    {
        var cache = new LibraryIndexCache();
        cache.GetBooks("");                       // forces the (empty) build
        foreach (var b in books) cache.UpdateBook(b);
        return cache;
    }

    private static List<LibraryBookDto> Search(LibraryIndexCache cache, Func<LibraryIndexCache,
        (List<LibraryBookDto> Books, int TotalCount, string[] AvailableGenres)> call) => call(cache).Books;

    // ------------------------------------------------------------ the basics

    [Fact]
    public void AnEmptyLibraryIsNotAnError()
    {
        var (books, total, genres) = CacheOf().SearchBooks("");

        books.Should().BeEmpty();
        total.Should().Be(0);
        genres.Should().BeEmpty();
    }

    /// <summary>
    /// The search box covers everything a person might half-remember about a book,
    /// not just its title.
    /// </summary>
    [Theory]
    [InlineData("dune")]          // title
    [InlineData("herbert")]       // author
    [InlineData("chronicles")]    // series
    [InlineData("sci-fi")]        // primary genre
    [InlineData("paperback")]     // a tag
    public void TheSearchTermMatchesEveryFieldAPersonMightRemember(string term)
    {
        var cache = CacheOf(
            Book("Dune", authors: ["Frank Herbert"], series: "Dune Chronicles",
                 primaryGenre: "Sci-Fi", tags: ["Paperback"]),
            Book("Something Else", authors: ["Nobody"]));

        var (books, _, _) = cache.SearchBooks("", searchTerm: term);

        books.Should().ContainSingle().Which.Title.Should().Be("Dune");
    }

    [Fact]
    public void TheSearchTermIgnoresCaseAndSurroundingSpace()
    {
        var cache = CacheOf(Book("Dune"), Book("Neuromancer"));

        cache.SearchBooks("", searchTerm: "  DUNE  ").Books
            .Should().ContainSingle().Which.Title.Should().Be("Dune");
    }

    // --------------------------------------------------------------- genres

    /// <summary>
    /// The sidebar's genre list is built before filtering, so choosing one genre
    /// does not collapse the list to only that genre and strand the user there.
    /// </summary>
    [Fact]
    public void TheGenreSidebarIsBuiltBeforeFilteringSoItDoesNotCollapse()
    {
        var cache = CacheOf(
            Book("A", primaryGenre: "Sci-Fi"),
            Book("B", primaryGenre: "History"));

        var (books, _, genres) = cache.SearchBooks("", genre: "Sci-Fi");

        books.Should().ContainSingle();
        genres.Should().BeEquivalentTo("History", "Sci-Fi");
    }

    /// <summary>
    /// Owner tags are a storage mechanism, not a genre. Leaking them into the
    /// sidebar would offer "Dad's Books" as something to browse by.
    /// </summary>
    [Fact]
    public void OwnerTagsAreNotOfferedAsGenres()
    {
        var cache = CacheOf(Book("A", primaryGenre: "Sci-Fi", tags: ["Dad's Books", "Hardback"]));

        var (_, _, genres) = cache.SearchBooks("");

        genres.Should().BeEquivalentTo("Sci-Fi", "Hardback");
    }

    /// <summary>A genre matches whether it is the primary one or merely a tag.</summary>
    [Fact]
    public void AGenreMatchesTheTagListAsWellAsThePrimaryGenre()
    {
        var cache = CacheOf(
            Book("Primary", primaryGenre: "Sci-Fi"),
            Book("Tagged", primaryGenre: "History", tags: ["Sci-Fi"]),
            Book("Neither", primaryGenre: "Cooking"));

        cache.SearchBooks("", genre: "sci-fi").Books
            .Select(b => b.Title).Should().BeEquivalentTo("Primary", "Tagged");
    }

    // ---------------------------------------------------------------- owners

    [Fact]
    public void TheOwnerFilterMatchesAnyOfTheSelectedTags()
    {
        var cache = CacheOf(
            Book("Dads", tags: ["Dad's Books"]),
            Book("Moms", tags: ["Mom's Books"]),
            Book("Nobodys", tags: []));

        cache.SearchBooks("", ownerTags: ["Dad's Books", "Mom's Books"]).Books
            .Select(b => b.Title).Should().BeEquivalentTo("Dads", "Moms");
    }

    /// <summary>
    /// Favourites are cross-referenced against whichever owner filter is active, so
    /// "Dad's books that Dad likes" does not also return the ones only Mom starred.
    /// </summary>
    [Fact]
    public void FavouritesAreScopedToTheActiveOwnerFilter()
    {
        var cache = CacheOf(
            Book("DadLikes", tags: ["Dad's Books"], favoritedBy: ["Dad"]),
            Book("MomLikes", tags: ["Dad's Books"], favoritedBy: ["Mom"]));

        cache.SearchBooks("", ownerTags: ["Dad's Books"], favoritesOnly: true).Books
            .Should().ContainSingle().Which.Title.Should().Be("DadLikes");
    }

    /// <summary>With no owner filter active, anyone's star counts.</summary>
    [Fact]
    public void WithNoOwnerFilterAnyHouseholdFavouriteCounts()
    {
        var cache = CacheOf(
            Book("MomLikes", favoritedBy: ["Mom"]),
            Book("Unloved", favoritedBy: []));

        cache.SearchBooks("", favoritesOnly: true).Books
            .Should().ContainSingle().Which.Title.Should().Be("MomLikes");
    }

    // -------------------------------------------------------- tidy-up filters

    /// <summary>
    /// The library-cleanup filters. Each one exists to surface books needing
    /// attention, so a book with a blank-but-present author must still count as
    /// missing — otherwise it hides from the very filter meant to find it.
    /// </summary>
    [Fact]
    public void AnAuthorThatIsPresentButBlankStillCountsAsMissing()
    {
        var cache = CacheOf(
            Book("NoAuthorArray", authors: []),
            Book("BlankAuthor", authors: ["   "]),
            Book("RealAuthor", authors: ["Ursula Le Guin"]));

        cache.SearchBooks("", missingAuthor: true).Books
            .Select(b => b.Title).Should().BeEquivalentTo("NoAuthorArray", "BlankAuthor");
    }

    [Fact]
    public void TheMissingCoverFilterFindsBlankAndAbsentCovers()
    {
        var cache = CacheOf(
            Book("NoCover", coverUrl: null),
            Book("BlankCover", coverUrl: "  "),
            Book("HasCover"));

        cache.SearchBooks("", missingCover: true).Books
            .Select(b => b.Title).Should().BeEquivalentTo("NoCover", "BlankCover");
    }

    /// <summary>
    /// The genre count is tags plus the primary genre, so a book with one tag and a
    /// primary genre counts as two — the filter is about how well-classified a book
    /// is, not how many tags it happens to carry.
    /// </summary>
    [Fact]
    public void TheGenreCountIncludesThePrimaryGenreNotJustTags()
    {
        var cache = CacheOf(
            Book("Bare", primaryGenre: null, tags: []),
            Book("PrimaryOnly", primaryGenre: "Sci-Fi", tags: []),
            Book("Both", primaryGenre: "Sci-Fi", tags: ["Space"]));

        cache.SearchBooks("", genreCountLessThan: 1).Books
            .Should().ContainSingle().Which.Title.Should().Be("Bare");

        cache.SearchBooks("", genreCountMoreThan: 1).Books
            .Should().ContainSingle().Which.Title.Should().Be("Both");
    }

    [Fact]
    public void TheRatingFiltersAreInclusiveOfTheirThreshold()
    {
        var cache = CacheOf(
            Book("Three", personalRating: 3, goodreads: 3.0),
            Book("Four", personalRating: 4, goodreads: 4.0),
            Book("Unrated"));

        cache.SearchBooks("", minPersonalRating: 4).Books
            .Should().ContainSingle().Which.Title.Should().Be("Four");

        cache.SearchBooks("", minGoodreadsRating: 4.0).Books
            .Should().ContainSingle().Which.Title.Should().Be("Four");
    }

    // ------------------------------------------- sorts that also filter

    /// <summary>
    /// Three sort modes quietly drop rows as well as reordering them, which is the
    /// least obvious behaviour in this method: choosing "series" hides every
    /// standalone book. That is intended — a series view of books with no series is
    /// empty rows — but it means the sort selector doubles as a filter, and nothing
    /// in the parameter name says so.
    /// </summary>
    [Theory]
    [InlineData("series", "InASeries")]
    [InlineData("stars", "Rated")]
    [InlineData("goodreads", "Reviewed")]
    public void TheSeriesStarsAndGoodreadsSortsAlsoFilterOutRowsTheyCannotShow(
        string sortBy, string expected)
    {
        var cache = CacheOf(
            Book("InASeries", series: "A Series"),
            Book("Rated", personalRating: 4),
            Book("Reviewed", goodreads: 4.2),
            Book("Plain"));

        cache.SearchBooks("", sortBy: sortBy).Books
            .Should().ContainSingle().Which.Title.Should().Be(expected);
    }

    [Fact]
    public void SortingByTitleIsCaseInsensitive()
    {
        var cache = CacheOf(Book("banana"), Book("Apple"), Book("cherry"));

        cache.SearchBooks("", sortBy: "title", sortDesc: false).Books
            .Select(b => b.Title).Should().ContainInOrder("Apple", "banana", "cherry");
    }

    // ------------------------------------------------------------ pagination

    /// <summary>
    /// The count is of everything that matched, not of the page returned — the
    /// frontend builds its pager from it, so a page-sized count would cap the
    /// library at one page.
    /// </summary>
    [Fact]
    public void TheTotalCountDescribesTheWholeMatchNotThePage()
    {
        var cache = CacheOf(Enumerable.Range(1, 10)
            .Select(i => Book($"Book {i:00}")).ToArray());

        var (books, total, _) = cache.SearchBooks("", sortBy: "title", sortDesc: false, skip: 0, take: 3);

        books.Should().HaveCount(3);
        total.Should().Be(10);
    }

    /// <summary>Zero means "all", which is how the export and count paths ask for everything.</summary>
    [Fact]
    public void ATakeOfZeroMeansEverything()
    {
        var cache = CacheOf(Enumerable.Range(1, 10).Select(i => Book($"Book {i:00}")).ToArray());

        cache.SearchBooks("", take: 0).Books.Should().HaveCount(10);
    }

    [Fact]
    public void SkippingPastTheEndIsEmptyRatherThanAnError()
    {
        var cache = CacheOf(Book("Only"));

        var (books, total, _) = cache.SearchBooks("", skip: 500);

        books.Should().BeEmpty();
        total.Should().Be(1, "the match is still one book; the page is just past it");
    }

    /// <summary>Filters combine as AND — each one narrows what the previous left.</summary>
    [Fact]
    public void FiltersCombineRatherThanOverride()
    {
        var cache = CacheOf(
            Book("Match", primaryGenre: "Sci-Fi", tags: ["Dad's Books"], personalRating: 5),
            Book("WrongGenre", primaryGenre: "History", tags: ["Dad's Books"], personalRating: 5),
            Book("WrongOwner", primaryGenre: "Sci-Fi", tags: ["Mom's Books"], personalRating: 5),
            Book("Underrated", primaryGenre: "Sci-Fi", tags: ["Dad's Books"], personalRating: 2));

        cache.SearchBooks("", genre: "Sci-Fi", ownerTags: ["Dad's Books"], minPersonalRating: 4).Books
            .Should().ContainSingle().Which.Title.Should().Be("Match");
    }

    // ------------------------------------------------- incremental updates

    /// <summary>
    /// An edit replaces the book in place rather than adding a second copy — the
    /// key is the file name, and a book that appeared twice after every save would
    /// be an obvious bug that nothing currently checks for.
    /// </summary>
    [Fact]
    public void EditingABookReplacesItRatherThanDuplicatingIt()
    {
        var cache = CacheOf(Book("Before", fileName: "same.epub"));

        cache.UpdateBook(Book("After", fileName: "same.epub"));

        cache.GetBooks("").Should().ContainSingle().Which.Title.Should().Be("After");
    }

    [Fact]
    public void RemovingABookIsCaseInsensitiveOnTheFileName()
    {
        var cache = CacheOf(Book("Gone", fileName: "Gone.epub"));

        cache.RemoveBook("gone.epub");

        cache.GetBooks("").Should().BeEmpty();
    }

    /// <summary>
    /// The cache is host-agnostic: two hosts asking for the same book each get
    /// their own.
    ///
    /// <para>This is the regression test for the cover-URL bug.
    /// <c>BuildIndex</c> used to take the <i>calling request's</i> base URL and
    /// build absolute cover URLs from it, and <c>RebuildCache</c> stored that as
    /// the shared cache — so whichever host triggered a rebuild had its hostname
    /// served to everyone else until the next one. Only the startup warm-up was
    /// safe, because it passed null.</para>
    ///
    /// <para>The fix removed the parameter entirely: <c>BuildIndex()</c> cannot be
    /// given a host, so it cannot bake one in. The host is applied per request by
    /// <c>NormalizeUrls</c> on the way out, which is what this asserts.</para>
    /// </summary>
    [Fact]
    public void TwoHostsEachGetTheirOwnCoverUrlsFromOneSharedCache()
    {
        var cache = CacheOf(Book("A", coverUrl: "/api/library/cover/a.jpg"));

        var fromA = cache.GetBooks("https://host-a.test").Single().CoverUrl;
        var fromB = cache.GetBooks("https://host-b.test").Single().CoverUrl;

        fromA.Should().Be("https://host-a.test/api/library/cover/a.jpg");
        fromB.Should().Be("https://host-b.test/api/library/cover/a.jpg",
            "the second caller must not inherit the first caller's hostname");
    }

    /// <summary>
    /// A genuinely external cover — OpenLibrary, Google Books — is passed through
    /// untouched, and that is correct rather than a trap: it is not ours to
    /// rewrite. This is the branch that made the old bug invisible, because a
    /// cover URL wrongly made absolute against one host was indistinguishable from
    /// a real external one.
    /// </summary>
    [Fact]
    public void AGenuinelyExternalCoverIsLeftAlone()
    {
        var cache = CacheOf(Book("A", coverUrl: "https://covers.openlibrary.org/b/id/1.jpg"));

        cache.GetBooks("https://host-b.test").Single()
            .CoverUrl.Should().Be("https://covers.openlibrary.org/b/id/1.jpg");
    }

    /// <summary>
    /// A relative path is completed against whoever is asking — the shape the
    /// warm-up produces, and now the only shape the cache ever holds.
    /// </summary>
    [Fact]
    public void ARelativeCoverPathIsCompletedAgainstTheRequestingHost()
    {
        var cache = CacheOf(Book("A", coverUrl: "/api/library/cover/a.jpg"));

        cache.GetBooks("https://host-b.test").Single()
            .CoverUrl.Should().Be("https://host-b.test/api/library/cover/a.jpg");
    }

    /// <summary>A newly added book lands in sort order, not on the end.</summary>
    [Fact]
    public void AnAddedBookIsPlacedInOrderNotAppended()
    {
        var cache = CacheOf(Book("Apple"), Book("Cherry"));

        cache.UpdateBook(Book("Banana"));

        cache.GetBooks("").Select(b => b.Title)
            .Should().ContainInOrder("Apple", "Banana", "Cherry");
    }
}
