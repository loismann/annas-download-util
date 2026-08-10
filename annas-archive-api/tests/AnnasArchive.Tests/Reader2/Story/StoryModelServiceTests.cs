using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Ingesting a chapter: what it costs, what it refuses, and what happens when two
/// of them arrive at once.
///
/// <para>Real storage, a real gateway, a fake model. The merge rules are covered
/// exhaustively in <see cref="StoryMergeTests"/>; these are the questions only the
/// assembled path can answer.</para>
/// </summary>
public class StoryModelServiceTests : IDisposable
{
    private readonly PipelineFixture _f = new();
    private readonly StoryModelService _story;

    public StoryModelServiceTests()
    {
        _story = _f.Story;
        _f.Ai.Answer = _ => """{"newActors": [{"canonicalName": "Pierre", "tier": "major"}]}""";
    }

    public void Dispose() => _f.Dispose();

    private int Extractions => _f.Ai.CallsOf(CallKind.StoryExtraction);

    /// <summary>A book read through a lens that builds a story model.</summary>
    private async Task<ReaderContext> BookAsync(string fileName = "novel.epub")
    {
        var book = await _f.Store.EnrolAsync(fileName, "epub bytes", "fiction");

        return PipelineFixture.Context((await _f.Store.Books.GetAsync(book))!, new FictionLens());
    }

    /// <summary>Writes a chapter summary straight to the store, as if one had been bought.</summary>
    private Task SummarisedAsync(ReaderContext ctx, int chapter) =>
        _f.Store.Artifacts.PutAsync(
            ArtifactKey.ChapterSummary(ctx.Ref, ctx.Lens.Key, chapter),
            new Prose($"What happens in chapter {chapter}."),
            new ArtifactProvenance(Prose.SchemaVersion, ctx.Lens.PromptVersion, "deep-model"));

    // ─── what it refuses ────────────────────────────────────────────────

    /// <summary>
    /// Ingest never summarises. Letting it would turn a cheap action into an
    /// expensive one without the reader asking for anything.
    /// </summary>
    [Fact]
    public async Task A_chapter_with_no_summary_is_skipped_rather_than_summarised()
    {
        var result = await _story.IngestAsync(await BookAsync(), chapter: 0);

        result.Skipped.Should().Be(IngestSkip.NoSummary);
        _f.Ai.Calls.Should().BeEmpty("nothing may reach a model here");
    }

    [Fact]
    public async Task A_book_type_that_builds_no_story_model_ingests_nothing()
    {
        var book = await _f.Store.EnrolAsync("ideas.epub", "epub bytes");
        var ctx = PipelineFixture.Context((await _f.Store.Books.GetAsync(book))!, new LiteraryLens());

        await SummarisedAsync(ctx, 0);

        (await _story.IngestAsync(ctx, 0)).Skipped.Should().Be(IngestSkip.NotAStoryLens);
        Extractions.Should().Be(0);
    }

    // ─── idempotency ────────────────────────────────────────────────────

    [Fact]
    public async Task Re_ingesting_a_chapter_costs_nothing()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);

        await _story.IngestAsync(ctx, 0);
        var again = await _story.IngestAsync(ctx, 0);

        Extractions.Should().Be(1, "chaptersIngested is what makes a back-fill resumable");
        again.Skipped.Should().Be(IngestSkip.AlreadyIngested);
        again.Model.Actors.Should().ContainSingle();
    }

    /// <summary>
    /// The lost-update trap, and the reason the keyed lock in the gateway is
    /// load-bearing rather than incidental: the whole model is one row, so two
    /// chapters merging at once would leave only the second one's work.
    /// </summary>
    [Fact]
    public async Task Two_chapters_ingesting_at_once_both_survive()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        await SummarisedAsync(ctx, 1);

        _f.Ai.Answer = call => call.UserPrompt.Contains("Chapter 1 summary")
            ? """{"newActors": [{"canonicalName": "Pierre", "tier": "major"}]}"""
            : """{"newActors": [{"canonicalName": "Natasha", "tier": "major"}]}""";

        await Task.WhenAll(
            _story.IngestAsync(ctx, 0),
            _story.IngestAsync(ctx, 1));

        var model = await _story.ReadAsync(ctx);

        model.Actors.Select(a => a.CanonicalName).Should().BeEquivalentTo(["Pierre", "Natasha"]);
        model.ChaptersIngested.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Two_ingests_of_the_same_chapter_at_once_buy_one_extraction()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);

        await Task.WhenAll(_story.IngestAsync(ctx, 0), _story.IngestAsync(ctx, 0));

        Extractions.Should().Be(1);
        (await _story.ReadAsync(ctx)).Actors.Should().ContainSingle();
    }

    // ─── what it costs ──────────────────────────────────────────────────

    /// <summary>
    /// One call over prose that is already a summary. This is the whole reason the
    /// story model is affordable at all, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task One_chapter_costs_exactly_one_fast_model_call()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 3);

        await _story.IngestAsync(ctx, 3);

        _f.Ai.Calls.Should().ContainSingle();
        _f.Ai.Calls.Single().Model.Should().Be("fast-model");
    }

    [Fact]
    public async Task The_extraction_is_told_the_digest_and_the_summary_and_nothing_else()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        await SummarisedAsync(ctx, 1);

        await _story.IngestAsync(ctx, 0);
        await _story.IngestAsync(ctx, 1);

        var second = _f.Ai.Calls.Last().UserPrompt;

        second.Should().Contain("Pierre", "the digest carries who is already known");
        second.Should().Contain("What happens in chapter 1.");
        second.Should().NotContain("What happens in chapter 0.", "an ingest re-reads no earlier chapter");
    }

    /// <summary>
    /// An unreadable answer still marks the chapter ingested. The household has
    /// paid for the call either way, and charging twice for the same unusable
    /// answer is the worse of the two failures.
    /// </summary>
    [Fact]
    public async Task An_unreadable_answer_costs_one_call_and_not_two()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        _f.Ai.Answer = _ => "I could not find any characters in this chapter.";

        await _story.IngestAsync(ctx, 0);
        await _story.IngestAsync(ctx, 0);

        Extractions.Should().Be(1);
        (await _story.ReadAsync(ctx)).ChaptersIngested.Should().Equal(0);
    }

    // ─── back-fill ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_back_fill_walks_only_the_chapters_that_have_summaries()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        await SummarisedAsync(ctx, 2);

        var model = await _story.BackFillAsync(ctx, chapterCount: 5);

        Extractions.Should().Be(2, "a chapter nobody has summarised is walked past, not summarised");
        model.ChaptersIngested.Should().Equal(0, 2);
    }

    [Fact]
    public async Task A_back_fill_resumes_rather_than_starting_again()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        await SummarisedAsync(ctx, 1);

        await _story.IngestAsync(ctx, 0);
        await _story.BackFillAsync(ctx, chapterCount: 2);

        Extractions.Should().Be(2, "the chapter already in costs nothing to walk past");
    }

    [Fact]
    public async Task A_back_fill_reports_a_step_for_every_chapter()
    {
        var ctx = await BookAsync();
        var steps = new List<ProgressStep>();

        await _story.BackFillAsync(
            ctx, chapterCount: 3, new Progress<ProgressStep>(steps.Add));

        // Progress<T> posts asynchronously; the count is what matters, not the timing.
        await Task.Delay(50);
        steps.Should().HaveCountGreaterThanOrEqualTo(1);
        steps.Should().OnlyContain(s => s.Total == 3);
    }

    // ─── reading and resolving ──────────────────────────────────────────

    [Fact]
    public async Task A_book_with_nothing_ingested_reads_as_empty_rather_than_failing()
    {
        (await _story.ReadAsync(await BookAsync())).Should().BeEquivalentTo(StoryModel.Empty);
    }

    [Fact]
    public async Task Accepting_a_question_is_stored()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        _f.Ai.Answer = _ => """
            {"newActors": [{"canonicalName": "Pyotr Bezukhov"}],
             "aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": "low"}]}
            """;

        await _story.IngestAsync(ctx, 0);
        var asked = await _story.ReadAsync(ctx);
        asked.CandidateMerges.Should().ContainSingle();

        var after = await _story.ResolveAsync(ctx, asked.CandidateMerges.Single().Id, accept: true);

        after.Actors.Single().Aliases.Should().Contain("Pierre");
        after.CandidateMerges.Single().Declined.Should().BeTrue("an answered question is not asked again");
        Extractions.Should().Be(1, "answering a question reaches no model");
    }

    /// <summary>
    /// Resolving is a read-modify-write of the same single row an ingest touches,
    /// so it takes the same lock — otherwise the two would overwrite each other.
    /// </summary>
    [Fact]
    public async Task Resolving_a_question_that_does_not_exist_changes_nothing()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        await _story.IngestAsync(ctx, 0);

        var after = await _story.ResolveAsync(ctx, "m99", accept: true);

        after.Actors.Should().ContainSingle();
        after.CandidateMerges.Should().BeEmpty();
    }

    /// <summary>
    /// The gate stops new spending, not the use of what has been bought. Answering
    /// a question reaches no model, and refusing it would leave a reader who cannot
    /// buy anything unable to correct the list they already own.
    /// </summary>
    [Fact]
    public async Task A_question_can_be_answered_with_the_allowance_exhausted()
    {
        var ctx = await AskedAsync();
        var asked = (await _story.ReadAsync(ctx)).CandidateMerges.Single().Id;

        _f.Usage.CostUsd = 9999;

        var after = await _story.ResolveAsync(ctx, asked, accept: true);

        after.Actors.Single().Aliases.Should().Contain("Pierre");
    }

    /// <summary>Ingesting is spending, and spending stops at the limit.</summary>
    [Fact]
    public async Task Folding_in_a_new_chapter_still_stops_at_the_allowance()
    {
        var ctx = await AskedAsync();
        await SummarisedAsync(ctx, 1);
        _f.Usage.CostUsd = 9999;

        var act = () => _story.IngestAsync(ctx, 1);

        await act.Should().ThrowAsync<TokenAllowanceException>();
    }

    /// <summary>A book with one chapter in and one question open on it.</summary>
    private async Task<ReaderContext> AskedAsync()
    {
        var ctx = await BookAsync();
        await SummarisedAsync(ctx, 0);
        _f.Ai.Answer = _ => """
            {"newActors": [{"canonicalName": "Pyotr Bezukhov"}],
             "aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": "low"}]}
            """;

        await _story.IngestAsync(ctx, 0);

        return ctx;
    }
}
