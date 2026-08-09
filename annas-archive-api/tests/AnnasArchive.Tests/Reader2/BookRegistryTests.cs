using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

public sealed class BookRegistryTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private static readonly ArtifactVersions V1 = new(1, 1);

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task An_enrolled_book_reads_back_with_its_metadata()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy", "fiction", title: "War and Peace");

        var enrolled = await _f.Books.GetAsync(book);

        enrolled.Should().NotBeNull();
        enrolled!.Title.Should().Be("War and Peace");
        enrolled.LensKey.Should().Be("fiction");
        enrolled.Authors.Should().Equal("An Author");
        enrolled.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Two_copies_of_one_book_under_different_names_are_one_enrolment()
    {
        var first = await _f.EnrolAsync("war-and-peace.epub", "identical bytes");
        _f.Library.Write("war-and-peace-copy.epub", "identical bytes");
        var second = (await _f.Hashes.GetAsync("war-and-peace-copy.epub"))!.Value;

        second.Should().Be(first);
        (await _f.Books.ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Setting_the_lens_does_not_disturb_either_lens_artifacts()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy", "literary");
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, "literary", 1),
            new TestPayload("literary summary"), new ArtifactProvenance(1, 1, "m"));

        await _f.Books.SetLensAsync(book, "fiction");
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, "fiction", 1),
            new TestPayload("fiction summary"), new ArtifactProvenance(1, 1, "m"));

        (await _f.Books.GetAsync(book))!.LensKey.Should().Be("fiction");
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(book, "literary", 1), V1))!
            .Content.Text.Should().Be("literary summary");
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(book, "fiction", 1), V1))!
            .Content.Text.Should().Be("fiction summary");
    }

    [Fact]
    public async Task Re_enrolling_keeps_the_lens_the_reader_chose()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy", "fiction");

        await _f.Books.EnrolAsync(book, "wp.epub", "War and Peace", ["Tolstoy"], "literary");

        (await _f.Books.GetAsync(book))!.LensKey.Should().Be("fiction");
    }

    // ─── the rename story ────────────────────────────────────────────────

    /// <summary>
    /// Reader I keys its cache on the sanitised path, so this exact sequence
    /// orphans every summary a book has. Here it must be invisible.
    /// </summary>
    [Fact]
    public async Task A_renamed_file_is_found_again_by_content_and_keeps_its_artifacts()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy bytes", "fiction");
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, "fiction", 1),
            new TestPayload("hard-won summary"), new ArtifactProvenance(1, 1, "m"));

        _f.Library.Rename("wp.epub", "War and Peace (Tolstoy).epub");

        var enrolled = await _f.Books.GetAsync(book);

        enrolled!.IsAvailable.Should().BeTrue();
        enrolled.FileName.Should().Be("War and Peace (Tolstoy).epub");
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(book, "fiction", 1), V1))!
            .Content.Text.Should().Be("hard-won summary");
    }

    [Fact]
    public async Task The_repaired_file_name_is_written_back_so_it_is_found_once_not_every_time()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy bytes");
        _f.Library.Rename("wp.epub", "renamed.epub");

        await _f.Books.GetAsync(book);
        _f.Library.Delete("renamed.epub");   // a later scan would now find nothing

        // Still reports the repaired name, proving the first resolve persisted it.
        (await _f.Books.GetAsync(book))!.FileName.Should().Be("renamed.epub");
    }

    [Fact]
    public async Task A_missing_file_marks_the_book_unavailable_and_keeps_every_artifact()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy bytes", "fiction");
        await _f.Artifacts.PutAsync(ArtifactKey.StoryModel(book, "fiction"),
            new TestPayload("300 characters of accumulated state"), new ArtifactProvenance(1, 1, "m"));

        _f.Library.Delete("wp.epub");

        var enrolled = await _f.Books.GetAsync(book);

        enrolled.Should().NotBeNull();
        enrolled!.IsAvailable.Should().BeFalse();
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.StoryModel(book, "fiction"), V1))
            .Should().NotBeNull("losing a novel's story model because a file moved is the loss this design prevents");
    }

    // ─── shelf ordering and removal ──────────────────────────────────────

    [Fact]
    public async Task The_shelf_lists_most_recently_opened_first_with_never_opened_last()
    {
        var a = await _f.EnrolAsync("a.epub", "aaa", title: "Alpha");
        var b = await _f.EnrolAsync("b.epub", "bbb", title: "Beta");
        await _f.EnrolAsync("c.epub", "ccc", title: "Gamma");

        await _f.Books.TouchOpenedAsync(a);
        await Task.Delay(10);
        await _f.Books.TouchOpenedAsync(b);

        var shelf = await _f.Books.ListAsync();

        shelf.Select(x => x.Title).Should().Equal("Beta", "Alpha", "Gamma");
    }

    [Fact]
    public async Task Removing_a_book_takes_its_artifacts_positions_bookmarks_and_text()
    {
        var book = await _f.EnrolAsync("wp.epub", "tolstoy", "fiction");
        await _f.Artifacts.PutAsync(ArtifactKey.ChapterSummary(book, "fiction", 1),
            new TestPayload("summary"), new ArtifactProvenance(1, 1, "m"));
        await _f.Text.WriteChapterAsync(book, 1, "chapter one text");

        (await _f.Books.RemoveAsync(book)).Should().BeTrue();

        (await _f.Books.GetAsync(book)).Should().BeNull();
        (await _f.Artifacts.GetAsync<TestPayload>(ArtifactKey.ChapterSummary(book, "fiction", 1), V1))
            .Should().BeNull();
        Directory.Exists(_f.Text.DirectoryFor(book)).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_an_unknown_book_is_a_harmless_false()
    {
        var absent = BookRef.Parse("ffffffffffffffff");
        (await _f.Books.RemoveAsync(absent)).Should().BeFalse();
    }
}

public sealed class ContentHashCacheTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task An_unchanged_file_is_hashed_once()
    {
        _f.Library.Write("wp.epub", "tolstoy");

        var first = await _f.Hashes.GetAsync("wp.epub");
        var second = await _f.Hashes.GetAsync("wp.epub");

        second.Should().Be(first);
        _f.Hashes.Count.Should().Be(1);
    }

    [Fact]
    public async Task An_edited_file_is_re_hashed()
    {
        _f.Library.Write("wp.epub", "first edition");
        var before = await _f.Hashes.GetAsync("wp.epub");

        await Task.Delay(10);
        _f.Library.Write("wp.epub", "second edition, quite different");

        (await _f.Hashes.GetAsync("wp.epub")).Should().NotBe(before);
    }

    [Fact]
    public async Task A_missing_file_hashes_to_null_rather_than_throwing()
    {
        (await _f.Hashes.GetAsync("not-there.epub")).Should().BeNull();
    }

    [Fact]
    public async Task FindFile_locates_a_book_by_its_contents()
    {
        _f.Library.Write("a.epub", "aaa");
        _f.Library.Write("b.epub", "bbb");
        var b = (await _f.Hashes.GetAsync("b.epub"))!.Value;

        (await _f.Hashes.FindFileAsync(b)).Should().Be("b.epub");
    }

    [Fact]
    public async Task FindFile_returns_null_when_nothing_matches()
    {
        _f.Library.Write("a.epub", "aaa");
        var absent = BookRef.Parse("ffffffffffffffff");

        (await _f.Hashes.FindFileAsync(absent)).Should().BeNull();
    }
}
