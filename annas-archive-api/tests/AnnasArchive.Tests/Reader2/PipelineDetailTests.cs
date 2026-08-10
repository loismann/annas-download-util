using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

public class PassageAndSectionTests : IDisposable
{
    private readonly PipelineFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// So the model does not re-explain what it explained two paragraphs ago.
    /// Read from the store by key range; Reader I globbed a directory and parsed
    /// word offsets back out of filenames.
    /// </summary>
    [Fact]
    public async Task A_passage_analysis_carries_the_earlier_ones_from_the_same_chapter()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        _f.Ai.Answer = call => "explanation of: " + call.UserPrompt.Split('\n')[^1];

        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 10, "the first passage"));
        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 500, "the second passage"));

        _f.Ai.Calls[^1].UserPrompt.Should()
            .Contain("Already explained earlier in this chapter")
            .And.Contain("explanation of: the first passage")
            .And.Contain("the second passage");
    }

    [Fact]
    public async Task An_earlier_passage_does_not_carry_a_later_one()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));

        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 500, "the later passage"));
        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 10, "the earlier passage"));

        _f.Ai.Calls[^1].UserPrompt.Should().NotContain("Already explained earlier");
    }

    /// <summary>Another chapter's analyses are not continuity for this one.</summary>
    [Fact]
    public async Task Continuity_does_not_cross_a_chapter_boundary()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(4, 100));
        await _f.Store.Text.WriteChapterAsync(ctx.Ref, 1, PipelineFixture.Paragraphs(4, 100));

        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 10, "chapter one passage"));
        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(1, 500, "chapter two passage"));

        _f.Ai.Calls[^1].UserPrompt.Should().NotContain("chapter one passage");
    }

    [Fact]
    public async Task A_section_summary_sends_only_that_section_s_words()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(20, 500));

        await _f.Pipeline.SummariseSectionAsync(ctx, 0, section: 1);

        var layout = await _f.Pipeline.LayoutAsync(ctx, 0);
        var sent = EpubTextExtractor.CountWords(_f.Ai.Calls[0].UserPrompt);

        sent.Should().Be(layout.Sections[1].WordCount);
        _f.Ai.Calls[0].UserPrompt.Should().NotContain("w0 ", "section 1 does not start at the beginning");
    }

    [Fact]
    public async Task Asking_for_a_section_that_does_not_exist_says_so_rather_than_calling_a_model()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        var act = () => _f.Pipeline.SummariseSectionAsync(ctx, 0, section: 99);

        await act.Should().ThrowAsync<ReaderAiException>().WithMessage("*no section 100*");
        _f.Ai.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unextracted_chapter_says_so_rather_than_calling_a_model()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        var act = () => _f.Pipeline.SummariseChapterAsync(ctx, chapter: 7);

        await act.Should().ThrowAsync<ReaderAiException>().WithMessage("*not been extracted*");
        _f.Ai.Calls.Should().BeEmpty();
    }

    /// <summary>Opening a chapter must never cost anything.</summary>
    [Fact]
    public async Task Working_out_the_layout_calls_no_model_and_ignores_the_allowance()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(20, 500));
        _f.Usage.CostUsd = 9999;

        var layout = await _f.Pipeline.LayoutAsync(ctx, 0);

        layout.Chunks.Should().NotBeEmpty();
        _f.Ai.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Boundaries are lens-independent. Renumbering sections under a reader would
    /// invalidate every section summary and bookmark that names one.
    /// </summary>
    [Fact]
    public async Task The_layout_survives_a_lens_switch_unchanged()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(20, 500));
        var before = await _f.Pipeline.LayoutAsync(ctx, 0);

        var switched = PipelineFixture.Context(ctx.Book, new TestLens());

        (await _f.Pipeline.LayoutAsync(switched, 0)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task The_model_tier_follows_configuration_per_call_kind()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 0, "a passage"));

        _f.Ai.Calls[0].Model.Should().Be("deep-model", "interpretive writing");
        _f.Ai.Calls[1].Model.Should().Be("fast-model", "short and bounded");
    }

    [Fact]
    public async Task Book_text_is_the_user_prompt_and_never_the_system_prompt()
    {
        var ctx = await _f.WithChapterAsync("A distinctive sentence nobody would write by accident.");

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        await _f.Pipeline.AnalysePassageAsync(ctx, new PassageRequest(0, 0, "A distinctive sentence"));

        _f.Ai.Calls.Should().OnlyContain(c => !c.SystemPrompt.Contains("distinctive sentence"));
        _f.Ai.Calls[0].UserPrompt.Should().Contain("distinctive sentence");
    }

    [Fact]
    public async Task Every_call_carries_the_budget_configured_for_its_kind()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        _f.Ai.Calls[0].MaxCompletionTokens
            .Should().Be(_f.Options[CallKind.ChapterSummary].MaxCompletionTokens);
    }

    /// <summary>
    /// Sending both silently switches reasoning off — a mistake Reader I's prompt
    /// code documents having made.
    /// </summary>
    [Fact]
    public async Task No_call_carries_both_a_temperature_and_a_reasoning_effort()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        await _f.Pipeline.ExplainSimplyAsync(ctx, 0);

        _f.Ai.Calls.Should().OnlyContain(c => c.Temperature == null || c.ReasoningEffort == null);
    }
}

public class ChapterLayoutTests
{
    [Fact]
    public void Sections_are_groups_of_chunks_and_cover_the_chapter()
    {
        var text = PipelineFixture.Paragraphs(20, 100);       // 2,000 words

        var layout = ChapterLayout.For(text, chunkWords: 200, chunksPerSection: 4);

        layout.Chunks.Should().HaveCount(10);
        layout.Sections.Should().HaveCount(3, "10 chunks in groups of four");
        layout.Sections[0].Start.Should().Be(0);
        layout.Sections[^1].End.Should().Be(2000);
    }

    [Fact]
    public void Sections_are_contiguous_with_no_gaps()
    {
        var layout = ChapterLayout.For(PipelineFixture.Paragraphs(30, 77), 300, 3);

        for (var i = 1; i < layout.Sections.Count; i++)
            layout.Sections[i].Start.Should().Be(layout.Sections[i - 1].End);
    }

    [Fact]
    public void An_empty_chapter_has_no_chunks_and_no_sections()
    {
        var layout = ChapterLayout.For("", 200, 4);

        layout.Chunks.Should().BeEmpty();
        layout.Sections.Should().BeEmpty();
    }

    [Fact]
    public void A_non_positive_grouping_is_rejected()
    {
        var act = () => ChapterLayout.For("some text", 200, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class TextSliceTests
{
    private const string Text = "One two three.\n\nFour five six.\n\nSeven eight nine.";

    [Theory]
    [InlineData(0, 3, "One two three.")]
    [InlineData(3, 3, "Four five six.")]
    [InlineData(6, 3, "Seven eight nine.")]
    public void A_slice_returns_exactly_its_words(int start, int count, string expected)
    {
        EpubTextExtractor.Slice(Text, start, count).Should().Be(expected);
    }

    /// <summary>
    /// The chunker splits on blank lines, so a slice that flattened them would
    /// hand the model a chapter with no structure in it.
    /// </summary>
    [Fact]
    public void A_slice_spanning_paragraphs_keeps_the_breaks()
    {
        EpubTextExtractor.Slice(Text, 0, 6).Should().Be("One two three.\n\nFour five six.");
    }

    [Fact]
    public void A_slice_past_the_end_stops_at_the_end()
    {
        EpubTextExtractor.Slice(Text, 6, 999).Should().Be("Seven eight nine.");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 3)]
    public void A_slice_of_nothing_is_empty_rather_than_an_error(int start, int count)
    {
        EpubTextExtractor.Slice(Text, start, count).Should().BeEmpty();
    }

    /// <summary>Word offsets in a slice must mean what they mean everywhere else.</summary>
    [Fact]
    public void Slicing_agrees_with_counting()
    {
        var text = PipelineFixture.Paragraphs(10, 37);

        EpubTextExtractor.CountWords(EpubTextExtractor.Slice(text, 100, 150)).Should().Be(150);
    }
}
