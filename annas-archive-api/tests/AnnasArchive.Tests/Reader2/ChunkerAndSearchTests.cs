using AnnasArchive.API.Reader2.Epub;

namespace AnnasArchive.Tests.Reader2;

public class SectionChunkerTests
{
    private static string Paragraphs(int count, int wordsEach) =>
        string.Join("\n\n", Enumerable.Range(0, count)
            .Select(p => string.Join(' ', Enumerable.Repeat($"w{p}", wordsEach))));

    [Fact]
    public void Sections_cover_the_whole_chapter_with_no_gaps_or_overlaps()
    {
        var text = Paragraphs(20, 100);          // 2,000 words
        var sections = SectionChunker.Detect(text, targetWords: 500);

        sections.Should().NotBeEmpty();
        sections[0].Start.Should().Be(0);
        sections[^1].End.Should().Be(2000);

        for (var i = 1; i < sections.Count; i++)
            sections[i].Start.Should().Be(sections[i - 1].End, "sections must be contiguous");
    }

    [Fact]
    public void Boundaries_land_on_paragraph_breaks()
    {
        var sections = SectionChunker.Detect(Paragraphs(20, 100), targetWords: 450);

        // Every paragraph is 100 words, so any legal boundary is a multiple of 100.
        sections.Select(s => s.Start).Should().OnlyContain(s => s % 100 == 0);
    }

    [Fact]
    public void An_empty_chapter_yields_no_sections()
    {
        SectionChunker.Detect("").Should().BeEmpty();
        SectionChunker.Detect("   \n\n  ").Should().BeEmpty();
    }

    [Fact]
    public void A_chapter_shorter_than_one_section_yields_exactly_one()
    {
        var sections = SectionChunker.Detect(Paragraphs(2, 10), targetWords: 2000);

        sections.Should().HaveCount(1);
        sections[0].Should().Be(new SectionBoundary(0, 20));
    }

    /// <summary>
    /// A single paragraph longer than the target has no break to land on, so the
    /// section runs long rather than cutting mid-sentence.
    /// </summary>
    [Fact]
    public void One_enormous_paragraph_becomes_one_long_section()
    {
        var sections = SectionChunker.Detect(Paragraphs(1, 5000), targetWords: 500);

        sections.Should().HaveCount(1);
        sections[0].WordCount.Should().Be(5000);
    }

    [Fact]
    public void The_result_is_deterministic()
    {
        var text = Paragraphs(30, 77);
        SectionChunker.Detect(text, 500).Should().BeEquivalentTo(SectionChunker.Detect(text, 500));
    }

    [Fact]
    public void A_non_positive_target_is_rejected_rather_than_looping_forever()
    {
        var act = () => SectionChunker.Detect("some text", targetWords: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class BookSearchTests
{
    private static readonly Chapter[] Chapters =
    [
        new(0, "One", 0, 10, "c0.xhtml"),
        new(1, "Two", 0, 10, "c1.xhtml"),
        new(2, "Three", 0, 10, "c2.xhtml")
    ];

    private static string? Text(Chapter c) => c.Id switch
    {
        0 => "Pierre entered the room. Pierre was uncertain.",
        1 => "Natasha danced all evening.",
        2 => "The regiment marched towards Moscow at dawn.",
        _ => null
    };

    [Fact]
    public void Matches_are_counted_per_chapter()
    {
        var hits = BookSearch.Run("Pierre", Chapters, Text);

        hits.Should().HaveCount(1);
        hits[0].ChapterId.Should().Be(0);
        hits[0].MatchCount.Should().Be(2);
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        BookSearch.Run("MOSCOW", Chapters, Text).Should().HaveCount(1);
    }

    [Fact]
    public void A_snippet_shows_the_match_in_context()
    {
        BookSearch.Run("regiment", Chapters, Text)[0].Snippet.Should().Contain("regiment marched");
    }

    [Fact]
    public void The_word_offset_lets_the_reader_be_paged_to_the_match()
    {
        BookSearch.Run("danced", Chapters, Text)[0].FirstWordOffset.Should().Be(1);
    }

    [Fact]
    public void No_matches_is_an_empty_list_not_an_error()
    {
        BookSearch.Run("Napoleon", Chapters, Text).Should().BeEmpty();
    }

    /// <summary>All three chapters contain "e", so an uncapped run returns three.</summary>
    [Fact]
    public void Results_are_capped()
    {
        BookSearch.Run("e", Chapters, Text).Should().HaveCount(3);
        BookSearch.Run("e", Chapters, Text, maxHits: 2).Should().HaveCount(2);
    }

    /// <summary>
    /// Reader I's ten-character floor blocks every one of these — the searches a
    /// reader of a long novel actually performs.
    /// </summary>
    [Theory]
    [InlineData("Pierre")]
    [InlineData("Moscow")]
    [InlineData("Rostov")]
    public void Character_and_place_names_are_long_enough_to_search(string term)
    {
        BookSearch.Validate(term).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void Queries_below_the_minimum_are_rejected(string term)
    {
        BookSearch.Validate(term).Should().NotBeNull();
    }

    [Fact]
    public void Queries_above_the_maximum_are_rejected()
    {
        BookSearch.Validate(new string('x', 501)).Should().NotBeNull();
    }

    [Fact]
    public void A_chapter_with_no_extracted_text_is_skipped_rather_than_failing()
    {
        BookSearch.Run("anything", Chapters, _ => null).Should().BeEmpty();
    }
}
