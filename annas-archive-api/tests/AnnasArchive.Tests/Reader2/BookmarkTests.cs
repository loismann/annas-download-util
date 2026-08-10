using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// A bookmark belongs to one reader and marks a place in the text — not in a
/// reading of it. Both halves of that sentence are load-bearing and neither is
/// visible from the schema alone, so both are pinned here.
/// </summary>
public class BookmarkTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly BookmarkStore _bookmarks;

    public BookmarkTests() => _bookmarks = new BookmarkStore(_f.Db);

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_book_with_no_bookmarks_lists_none_rather_than_failing()
    {
        var book = await _f.EnrolAsync("clean.epub", "contents");

        (await _bookmarks.ListAsync(book, "paul")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_bookmark_round_trips_with_its_label()
    {
        var book = await _f.EnrolAsync("marked.epub", "contents");

        var saved = await _bookmarks.SaveAsync(book, "paul", 3, 250, "the argument turns");

        saved.Chapter.Should().Be(3);
        saved.WordOffset.Should().Be(250);
        saved.Label.Should().Be("the argument turns");
        saved.Id.Should().NotBeEmpty();

        (await _bookmarks.ListAsync(book, "paul")).Should().ContainSingle()
            .Which.Id.Should().Be(saved.Id);
    }

    /// <summary>
    /// The bar shows them in the order they are met in the book, which is not the
    /// order they were saved in — a reader marks a passage, then goes back for one
    /// they passed earlier.
    /// </summary>
    [Fact]
    public async Task Bookmarks_come_back_in_reading_order_not_creation_order()
    {
        var book = await _f.EnrolAsync("ordered.epub", "contents");

        await _bookmarks.SaveAsync(book, "paul", 4, 100, null);
        await _bookmarks.SaveAsync(book, "paul", 1, 900, null);
        await _bookmarks.SaveAsync(book, "paul", 1, 50, null);

        (await _bookmarks.ListAsync(book, "paul"))
            .Select(b => (b.Chapter, b.WordOffset))
            .Should().Equal((1, 50), (1, 900), (4, 100));
    }

    /// <summary>
    /// The control is a toggle on the page in front of the reader. Pressing it
    /// twice must not put the same page in the bar twice.
    /// </summary>
    [Fact]
    public async Task Marking_one_place_twice_updates_the_mark_rather_than_adding_a_second()
    {
        var book = await _f.EnrolAsync("twice.epub", "contents");

        var first = await _bookmarks.SaveAsync(book, "paul", 2, 400, "first thought");
        var again = await _bookmarks.SaveAsync(book, "paul", 2, 400, "better thought");

        again.Id.Should().Be(first.Id, "it is the same place, so it is the same mark");
        again.CreatedAtUtc.Should().Be(first.CreatedAtUtc, "re-labelling does not re-date it");

        var all = await _bookmarks.ListAsync(book, "paul");
        all.Should().ContainSingle().Which.Label.Should().Be("better thought");
    }

    [Fact]
    public async Task A_bookmark_is_removed_by_its_id()
    {
        var book = await _f.EnrolAsync("removable.epub", "contents");
        var saved = await _bookmarks.SaveAsync(book, "paul", 1, 10, null);

        (await _bookmarks.RemoveAsync(book, "paul", saved.Id)).Should().BeTrue();
        (await _bookmarks.ListAsync(book, "paul")).Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_bookmark_that_is_not_there_reports_it_rather_than_pretending()
    {
        var book = await _f.EnrolAsync("absent.epub", "contents");

        (await _bookmarks.RemoveAsync(book, "paul", "no-such-id")).Should().BeFalse();
    }

    [Fact]
    public async Task Two_readers_of_one_book_do_not_see_each_others_marks()
    {
        var book = await _f.EnrolAsync("household.epub", "contents");

        var mine = await _bookmarks.SaveAsync(book, "paul", 1, 10, "mine");
        await _bookmarks.SaveAsync(book, "someone-else", 5, 60, "theirs");

        (await _bookmarks.ListAsync(book, "paul")).Should().ContainSingle()
            .Which.Label.Should().Be("mine");

        (await _bookmarks.RemoveAsync(book, "someone-else", mine.Id))
            .Should().BeFalse("a mark somebody else owns does not exist from here");
    }

    /// <summary>
    /// Two readers may mark the same sentence. The uniqueness rule is per reader,
    /// not per place, and a shared index that got that wrong would silently hand
    /// one reader the other's row.
    /// </summary>
    [Fact]
    public async Task Two_readers_may_mark_the_very_same_place()
    {
        var book = await _f.EnrolAsync("same-place.epub", "contents");

        var mine = await _bookmarks.SaveAsync(book, "paul", 2, 400, "mine");
        var theirs = await _bookmarks.SaveAsync(book, "someone-else", 2, 400, "theirs");

        theirs.Id.Should().NotBe(mine.Id);
        (await _bookmarks.ListAsync(book, "paul")).Should().ContainSingle()
            .Which.Label.Should().Be("mine");
    }

    /// <summary>
    /// Bookmarks carry no <c>lens_key</c>, so this holds by construction — which is
    /// exactly why it is worth a test. Someone adding lens scoping later to make
    /// bookmarks "match the reading" would break a reader's marks on every book
    /// type change, and nothing else would notice.
    /// </summary>
    [Fact]
    public async Task Changing_the_book_type_keeps_every_mark()
    {
        var book = await _f.EnrolAsync("switched.epub", "contents");
        await _bookmarks.SaveAsync(book, "paul", 3, 250, "kept");

        await _f.Books.SetLensAsync(book, TestLens.LensKey);

        (await _bookmarks.ListAsync(book, "paul")).Should().ContainSingle()
            .Which.Label.Should().Be("kept");
    }

    [Fact]
    public async Task Un_enrolling_a_book_takes_its_bookmarks_with_it()
    {
        var book = await _f.EnrolAsync("gone.epub", "contents");
        await _bookmarks.SaveAsync(book, "paul", 3, 250, "doomed");

        await _f.Books.RemoveAsync(book);

        (await _bookmarks.ListAsync(book, "paul")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_mark_with_no_label_stays_unlabelled_rather_than_becoming_empty_text()
    {
        var book = await _f.EnrolAsync("plain.epub", "contents");

        (await _bookmarks.SaveAsync(book, "paul", 1, 0, null)).Label.Should().BeNull();
        (await _bookmarks.ListAsync(book, "paul")).Single().Label.Should().BeNull();
    }
}
