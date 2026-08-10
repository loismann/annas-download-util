using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

public sealed class BookIngestorTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly BookIngestor _ingestor;

    public BookIngestorTests() =>
        _ingestor = new BookIngestor(_f.Library, _f.Text, _f.Artifacts);

    public void Dispose() => _f.Dispose();

    private Task<EnrolledBook> EnrolAsync(byte[] epub, string fileName = "book.epub") =>
        _f.EnrolEpubAsync(epub, fileName);

    [Fact]
    public async Task Ingesting_writes_every_chapter_and_the_index()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());

        var index = await _ingestor.IngestAsync(book);

        index.Chapters.Should().HaveCount(3);
        _f.Text.ExtractedChapters(book.Book).Should().Equal(0, 1, 2);
        (await _f.Text.ReadChapterAsync(book.Book, 0)).Should().Contain("bright cold day");
    }

    [Fact]
    public async Task Progress_is_reported_for_every_chapter_and_ends_complete()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        var progress = new ProgressRecorder<ProgressStep>();

        var index = await _ingestor.IngestAsync(book, progress);

        progress.Steps.Count(s => s.Stage == "extracting").Should().Be(index.Chapters.Count);
        progress.Steps[^1].Stage.Should().Be("complete");
    }

    [Fact]
    public async Task Ingesting_twice_yields_the_same_index_without_re_extracting()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        var first = await _ingestor.IngestAsync(book);

        // If the second run re-extracted, it would overwrite this marker.
        await _f.Text.WriteChapterAsync(book.Book, 0, "SENTINEL");
        var second = await _ingestor.IngestAsync(book);

        second.Should().BeEquivalentTo(first);
        (await _f.Text.ReadChapterAsync(book.Book, 0)).Should().Be("SENTINEL");
    }

    /// <summary>
    /// The index is the commit point. A chapter file lost after the fact must
    /// make the book count as un-ingested so the next open repairs it.
    /// </summary>
    [Fact]
    public async Task A_missing_chapter_file_makes_the_ingest_resume_and_repair()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        await _ingestor.IngestAsync(book);

        File.Delete(_f.Text.ChapterFile(book.Book, 1));
        (await _ingestor.CompleteIndexAsync(book.Book)).Should().BeNull("the pair has come apart");

        await _ingestor.IngestAsync(book);

        _f.Text.ExtractedChapters(book.Book).Should().Equal(0, 1, 2);
        (await _ingestor.CompleteIndexAsync(book.Book)).Should().NotBeNull();
    }

    [Fact]
    public async Task Force_re_extracts_even_when_the_index_is_complete()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        await _ingestor.IngestAsync(book);
        await _f.Text.WriteChapterAsync(book.Book, 0, "SENTINEL");

        await _ingestor.IngestAsync(book, force: true);

        (await _f.Text.ReadChapterAsync(book.Book, 0)).Should().NotBe("SENTINEL");
    }

    [Fact]
    public async Task A_corrupt_book_fails_with_a_reader_facing_message_and_writes_no_index()
    {
        var book = await EnrolAsync(EpubBuilder.Corrupt(), "broken.epub");

        var act = () => _ingestor.IngestAsync(book);

        await act.Should().ThrowAsync<EpubException>();
        (await _ingestor.CompleteIndexAsync(book.Book)).Should().BeNull();
    }

    [Fact]
    public async Task A_book_whose_file_has_gone_fails_by_name()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        _f.Library.Delete(book.FileName);

        var act = () => _ingestor.IngestAsync(book);

        await act.Should().ThrowAsync<EpubException>().WithMessage("*no longer in the library*");
    }

    [Fact]
    public async Task The_index_is_stored_lens_independently_so_a_lens_switch_never_re_extracts()
    {
        var book = await EnrolAsync(EpubBuilder.Epub3WithNav());
        await _ingestor.IngestAsync(book);

        await _f.Books.SetLensAsync(book.Book, "fiction");

        (await _ingestor.CompleteIndexAsync(book.Book)).Should().NotBeNull();
    }

    [Fact]
    public async Task Two_copies_of_one_book_share_a_single_extraction()
    {
        var first = await EnrolAsync(EpubBuilder.Epub3WithNav(), "copy-one.epub");
        await _ingestor.IngestAsync(first);

        _f.Library.WriteBytes("copy-two.epub", EpubBuilder.Epub3WithNav());
        var secondId = (await _f.Hashes.GetAsync("copy-two.epub"))!.Value;

        secondId.Should().Be(first.Book);
        _f.Text.DirectoryFor(secondId).Should().Be(_f.Text.DirectoryFor(first.Book));
    }

    [Fact]
    public async Task Every_fixture_ingests()
    {
        (byte[] Epub, string Name)[] fixtures =
        [
            (EpubBuilder.Epub3WithNav(), "epub3.epub"),
            (EpubBuilder.Epub2WithNcxOnly(), "epub2.epub"),
            (EpubBuilder.NoToc(), "no-toc.epub"),
            (EpubBuilder.NestedTocDepthThree(), "nested.epub"),
            (EpubBuilder.NonAsciiFileNames(), "non-ascii.epub"),
            (EpubBuilder.MisdeclaredMimetype(), "bad-mimetype.epub"),
            (EpubBuilder.MissingContainer(), "no-container.epub"),
            (EpubBuilder.MissingMediaTypes(), "no-media-types.epub"),
            (EpubBuilder.PercentEncodedHrefs(), "escaped-hrefs.epub")
        ];

        foreach (var (epub, name) in fixtures)
        {
            var book = await EnrolAsync(epub, name);
            var index = await _ingestor.IngestAsync(book);

            index.Chapters.Should().NotBeEmpty($"{name} should produce chapters");
            _f.Text.ExtractedChapters(book.Book).Should().HaveCount(index.Chapters.Count, name);
        }
    }
}
