using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.Tests.Reader2;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// The model feeding back into summaries (spec Phase 9), proven through the
/// assembled pipeline: a summary of a later chapter is written knowing who has
/// appeared and what is running, because the record rode along with the text.
/// </summary>
public class StoryFedSummaryTests : IDisposable
{
    private readonly PipelineFixture _f = new();

    public StoryFedSummaryTests()
    {
        _f.Ai.Answer = call => call.SystemPrompt.Contains("running record")
            ? """
              {"newActors": [{"canonicalName": "Dolokhov", "tier": "major"}],
               "newThreads": [{"name": "The debt", "participantIds": [], "firstBeat": "the game"}]}
              """
            : "prose";
    }

    public void Dispose() => _f.Dispose();

    private async Task<ReaderContext> FictionAsync(string chapterText, int chapter)
    {
        var ctx = await _f.WithChapterAsync(chapterText, chapter, "novel.epub");

        return PipelineFixture.Context(ctx.Book, new FictionLens());
    }

    private string FinalSummaryInput() =>
        _f.Ai.Calls.Last(c => c.SystemPrompt.Contains("eight headings")).UserPrompt;

    [Fact]
    public async Task A_later_summary_is_told_who_appeared_and_what_still_runs()
    {
        var ctx = await FictionAsync("Chapter one text.", 0);
        await _f.Store.Text.WriteChapterAsync(ctx.Ref, 1, "Chapter two text.");

        // Chapter 0: summarised and ingested, as the summary route does.
        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        await _f.Story.IngestAsync(ctx, 0);

        await _f.Pipeline.SummariseChapterAsync(ctx, 1);

        var final = FinalSummaryInput();

        final.Should().Contain("Dolokhov", "the who-appears reminders come from the record");
        final.Should().Contain("The debt", "the parallel threads come from the record");
        final.Should().Contain("## This chapter", "the record must not blur into the chapter itself");
    }

    [Fact]
    public async Task The_first_summary_of_a_book_carries_no_record_because_there_is_none()
    {
        var ctx = await FictionAsync("Chapter one text.", 0);

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        FinalSummaryInput().Should().NotContain("running record");
    }

    [Fact]
    public async Task A_book_type_with_no_story_model_summarises_from_the_text_alone()
    {
        var ctx = await _f.WithChapterAsync("Some philosophy.", 0);

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        _f.Ai.Calls.Single().UserPrompt.Should().NotContain("running record");
    }

    /// <summary>
    /// Only the final call is fed the record. The lower rungs record the text in
    /// front of them, and feeding a record into thirty chunk calls would multiply
    /// its token cost by thirty for headings that are written once.
    /// </summary>
    [Fact]
    public async Task The_lower_rungs_never_see_the_record()
    {
        var ctx = await FictionAsync(PipelineFixture.Paragraphs(12, 500), 0);
        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        await _f.Story.IngestAsync(ctx, 0);

        _f.Ai.Calls.Clear();
        await _f.Store.Text.WriteChapterAsync(ctx.Ref, 1, PipelineFixture.Paragraphs(12, 500));
        await _f.Pipeline.SummariseChapterAsync(ctx, 1);

        var lowerRungs = _f.Ai.Calls.Where(c => !c.SystemPrompt.Contains("eight headings"));

        lowerRungs.Should().NotBeEmpty();
        lowerRungs.Should().OnlyContain(c => !c.UserPrompt.Contains("running record"));
        FinalSummaryInput().Should().Contain("Dolokhov");
    }
}
