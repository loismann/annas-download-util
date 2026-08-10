using AnnasArchive.API.Reader2.Epub;

namespace AnnasArchive.Tests.Reader2;

public class EpubPackageTests
{
    private static EpubPackage Open(byte[] epub) => EpubPackage.Open(new MemoryStream(epub));

    [Fact]
    public void An_epub3_book_yields_its_metadata_and_spine()
    {
        using var package = Open(EpubBuilder.Epub3WithNav());

        package.Title.Should().Be("An EPUB 3 Book");
        package.Authors.Should().Equal("Test Author");
        package.Spine.Should().HaveCount(3);
        package.NavigationDocument.Should().NotBeNull();
    }

    [Fact]
    public void An_epub2_book_exposes_its_ncx_and_no_nav_document()
    {
        using var package = Open(EpubBuilder.Epub2WithNcxOnly());

        package.Ncx.Should().NotBeNull();
        package.NavigationDocument.Should().BeNull();
        package.Spine.Should().HaveCount(2);
    }

    /// <summary>Every e-reader ignores a wrong mimetype; rejecting the book would
    /// be stricter than the format is in practice.</summary>
    [Fact]
    public void A_misdeclared_mimetype_does_not_stop_the_book_opening()
    {
        using var package = Open(EpubBuilder.MisdeclaredMimetype());
        package.Spine.Should().HaveCount(1);
    }

    [Fact]
    public void A_missing_container_falls_back_to_finding_the_package_file()
    {
        using var package = Open(EpubBuilder.MissingContainer());

        package.Title.Should().Be("A Book With No Container");
        package.Spine.Should().HaveCount(1);
    }

    /// <summary>
    /// An item with no declared media-type must not fall out of the spine — that
    /// turns a cosmetic defect into "this book has no chapters".
    /// </summary>
    [Fact]
    public void Undeclared_media_types_fall_back_to_the_href_extension()
    {
        using var package = Open(EpubBuilder.MissingMediaTypes());
        package.Spine.Should().HaveCount(2);
    }

    [Fact]
    public void Percent_encoded_hrefs_resolve_to_their_literal_entries()
    {
        using var package = Open(EpubBuilder.PercentEncodedHrefs());

        package.Spine.Should().HaveCount(1);
        package.ReadText(package.Spine[0]).Should().Contain("despite the encoding");
    }

    [Fact]
    public void Non_ascii_file_names_resolve()
    {
        using var package = Open(EpubBuilder.NonAsciiFileNames());

        package.Title.Should().Be("Война и миръ");
        package.Spine.Should().HaveCount(2);
        package.ReadText(package.Spine[0]).Should().Contain("князь");
    }

    /// <summary>A corrupt archive must reach the reader as a sentence, not a stack trace.</summary>
    [Fact]
    public void A_corrupt_archive_fails_with_a_reader_facing_message()
    {
        var act = () => Open(EpubBuilder.Corrupt());

        act.Should().Throw<EpubException>()
            .WithMessage("*not a readable EPUB archive*");
    }
}

public class EpubNavigationTests
{
    [Fact]
    public void Titles_are_read_from_an_epub3_nav_document()
    {
        using var package = EpubPackage.Open(new MemoryStream(EpubBuilder.Epub3WithNav()));

        EpubNavigation.Read(package).Select(e => e.Title)
            .Should().Equal("The Beginning", "The Middle", "The End");
    }

    [Fact]
    public void Titles_are_read_from_an_epub2_ncx()
    {
        using var package = EpubPackage.Open(new MemoryStream(EpubBuilder.Epub2WithNcxOnly()));

        EpubNavigation.Read(package).Select(e => e.Title)
            .Should().Equal("First Part", "Second Part");
    }

    [Fact]
    public void Nesting_depth_survives_three_levels()
    {
        using var package = EpubPackage.Open(new MemoryStream(EpubBuilder.NestedTocDepthThree()));

        EpubNavigation.Read(package).Select(e => e.Level).Should().Equal(0, 1, 2, 0);
    }

    [Fact]
    public void A_book_with_no_contents_yields_no_entries_rather_than_failing()
    {
        using var package = EpubPackage.Open(new MemoryStream(EpubBuilder.NoToc()));
        EpubNavigation.Read(package).Should().BeEmpty();
    }
}

public class EpubTextExtractorTests
{
    [Fact]
    public void Markup_is_stripped_and_entities_decoded()
    {
        EpubTextExtractor.ToPlainText("<p>Tom &amp; Jerry <em>ran</em>.</p>")
            .Should().Be("Tom & Jerry ran.");
    }

    [Fact]
    public void Scripts_and_styles_are_removed_entirely()
    {
        var text = EpubTextExtractor.ToPlainText(
            "<style>.a{color:red}</style><p>Visible.</p><script>alert(1)</script>");

        text.Should().Be("Visible.");
    }

    /// <summary>Paragraph breaks are load-bearing: the chunker splits on them.</summary>
    [Fact]
    public void Paragraph_breaks_are_preserved_as_blank_lines()
    {
        EpubTextExtractor.ToPlainText("<p>One.</p><p>Two.</p>").Should().Be("One.\n\nTwo.");
    }

    [Fact]
    public void Runs_of_blank_lines_collapse_to_one()
    {
        EpubTextExtractor.ToPlainText("<div><p>One.</p></div><div><p>Two.</p></div>")
            .Split("\n\n").Should().HaveCount(2);
    }

    [Fact]
    public void Non_breaking_spaces_become_ordinary_spaces_so_words_split()
    {
        EpubTextExtractor.CountWords(EpubTextExtractor.ToPlainText("<p>a&nbsp;b&nbsp;c</p>"))
            .Should().Be(3);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("one two three", 3)]
    [InlineData("one\n\ntwo", 2)]
    public void Word_counting_is_whitespace_separated(string text, int expected)
    {
        EpubTextExtractor.CountWords(text).Should().Be(expected);
    }

    [Fact]
    public void Empty_input_yields_empty_output_rather_than_throwing()
    {
        EpubTextExtractor.ToPlainText("").Should().BeEmpty();
    }
}

public class ChapterIndexBuilderTests
{
    private static (ChapterIndex Index, IReadOnlyList<ExtractedChapter> Chapters) Build(byte[] epub)
    {
        using var package = EpubPackage.Open(new MemoryStream(epub));
        return ChapterIndexBuilder.Build(package);
    }

    [Fact]
    public void Chapters_come_back_in_spine_order_with_toc_titles()
    {
        var (index, _) = Build(EpubBuilder.Epub3WithNav());

        index.Title.Should().Be("An EPUB 3 Book");
        index.Chapters.Select(c => c.Id).Should().Equal(0, 1, 2);
        index.Chapters.Select(c => c.Title).Should().Equal("The Beginning", "The Middle", "The End");
    }

    [Fact]
    public void Titles_come_from_the_ncx_when_that_is_all_there_is()
    {
        var (index, _) = Build(EpubBuilder.Epub2WithNcxOnly());
        index.Chapters.Select(c => c.Title).Should().Equal("First Part", "Second Part");
    }

    /// <summary>
    /// The spine is authoritative for existence. A TOC-driven index would return
    /// nothing here, losing the whole book.
    /// </summary>
    [Fact]
    public void A_book_with_no_toc_still_produces_every_chapter()
    {
        var (index, _) = Build(EpubBuilder.NoToc());

        index.Chapters.Should().HaveCount(2);
        index.Chapters.Should().OnlyContain(c => c.Title.Length > 0);
    }

    [Fact]
    public void Nesting_depth_is_carried_onto_the_chapters()
    {
        var (index, _) = Build(EpubBuilder.NestedTocDepthThree());
        index.Chapters.Select(c => c.Level).Should().Equal(0, 1, 2, 0);
    }

    [Fact]
    public void Word_counts_are_recorded_and_summed()
    {
        var (index, chapters) = Build(EpubBuilder.Epub3WithNav());

        index.Chapters.Should().OnlyContain(c => c.WordCount > 0);
        index.TotalWords.Should().Be(chapters.Sum(c => c.Chapter.WordCount));
    }

    [Fact]
    public void Extracted_text_accompanies_the_index_so_no_second_parse_is_needed()
    {
        var (_, chapters) = Build(EpubBuilder.Epub3WithNav());
        chapters[0].Text.Should().Contain("bright cold day");
    }
}
