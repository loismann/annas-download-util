using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The ladder, the cache, and the bill. Every test here counts model calls,
/// because the failures worth catching in this layer are all "it charged the
/// reader for something".
/// </summary>
public class PipelineTests : IDisposable
{
    private readonly PipelineFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// N chunks costs N tier-1 calls, ⌈N/4⌉ tier-2 calls, and one tier-3 call.
    /// Reader I's own comments record double-billing a tier once already.
    /// </summary>
    [Fact]
    public async Task A_long_chapter_bills_one_call_per_chunk_per_group_and_one_final()
    {
        // 12 paragraphs of 500 words = 6,000 words → 3 chunks at the 2,000 default.
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(12, 500));

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        var chunks = (await _f.Pipeline.LayoutAsync(ctx, 0)).Chunks.Count;
        var groups = (int)Math.Ceiling(chunks / (double)_f.Options.ChunksPerSection);

        _f.Ai.CallsOf(CallKind.ChunkSummary).Should().Be(chunks);
        _f.Ai.CallsOf(CallKind.SectionSynthesis).Should().Be(groups);
        _f.Ai.CallsOf(CallKind.ChapterSummary).Should().Be(1);
        _f.Ai.Calls.Should().HaveCount(chunks + groups + 1);
    }

    /// <summary>
    /// A 200-word interstitial chapter — and a long novel has many — otherwise
    /// costs three calls and emits more summary than it had text.
    /// </summary>
    [Fact]
    public async Task A_short_chapter_skips_the_ladder_entirely()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        _f.Ai.Calls.Should().HaveCount(1);
        _f.Ai.CallsOf(CallKind.ChapterSummary).Should().Be(1);
    }

    [Fact]
    public async Task The_threshold_is_configurable_and_load_bearing()
    {
        using var strict = new PipelineFixture(
            new Dictionary<string, string?> { ["Reader2:DirectSummaryWordThreshold"] = "10" });

        var ctx = await strict.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        await strict.Pipeline.SummariseChapterAsync(ctx, 0);

        strict.Ai.Calls.Should().HaveCountGreaterThan(1, "200 words is now above the threshold");
    }

    [Fact]
    public async Task A_second_request_is_served_from_the_store_and_bills_nothing()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        var first = await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        var callsAfterFirst = _f.Ai.Calls.Count;
        var second = await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        second.Should().Be(first);
        _f.Ai.Calls.Should().HaveCount(callsAfterFirst);
    }

    [Fact]
    public async Task Force_regenerates_and_overwrites_rather_than_adding_a_row()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));
        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        _f.Ai.Answer = _ => "a different summary";
        var forced = await _f.Pipeline.SummariseChapterAsync(ctx, 0, force: true);

        forced.Markdown.Should().Be("a different summary");
        _f.Ai.Calls.Should().HaveCount(2);

        var rows = await _f.Store.Artifacts.ListAsync<Prose>(
            new ArtifactQuery(ctx.Ref, ctx.Lens.Key, ArtifactKind.ChapterSummary),
            new ArtifactVersions(Prose.SchemaVersion, ctx.Lens.PromptVersion));

        rows.Should().HaveCount(1, "force overwrites; it does not accumulate");
        rows[0].Content.Markdown.Should().Be("a different summary");
    }

    /// <summary>Two tabs, one chapter. The loser of the race must not pay again.</summary>
    [Fact]
    public async Task Concurrent_requests_for_one_chapter_produce_one_artifact_and_one_bill()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _f.Pipeline.SummariseChapterAsync(ctx, 0)));

        _f.Ai.Calls.Should().HaveCount(1);
        results.Should().OnlyContain(r => r == results[0]);
    }

    [Fact]
    public async Task An_exhausted_allowance_stops_the_call_before_a_token_is_spent()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));
        _f.Usage.CostUsd = 9999;

        var act = () => _f.Pipeline.SummariseChapterAsync(ctx, 0);

        await act.Should().ThrowAsync<TokenAllowanceException>();
        _f.Ai.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Work already paid for stays readable at the limit. Only new spending stops.
    /// </summary>
    [Fact]
    public async Task An_exhausted_allowance_still_serves_what_is_already_stored()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));
        var summary = await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        _f.Usage.CostUsd = 9999;

        (await _f.Pipeline.SummariseChapterAsync(ctx, 0)).Should().Be(summary);
    }

    [Fact]
    public async Task A_failed_model_call_leaves_no_partial_row()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(12, 500));
        _f.Ai.FailOnCall = 2;

        var act = () => _f.Pipeline.SummariseChapterAsync(ctx, 0);

        await act.Should().ThrowAsync<ReaderAiException>();

        var stored = await _f.Store.Artifacts.GetAsync<Prose>(
            ArtifactKey.ChapterSummary(ctx.Ref, ctx.Lens.Key, 0),
            new ArtifactVersions(Prose.SchemaVersion, ctx.Lens.PromptVersion));

        stored.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_summary_leaves_no_partial_row_and_the_next_attempt_starts_clean()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(12, 500));
        using var cancelled = new CancellationTokenSource();
        _f.Ai.Answer = call => { cancelled.Cancel(); return "partial"; };

        var act = () => _f.Pipeline.SummariseChapterAsync(ctx, 0, ct: cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _f.Ai.Answer = _ => "complete";
        (await _f.Pipeline.SummariseChapterAsync(ctx, 0)).Markdown.Should().Be("complete");
    }

    [Fact]
    public async Task Progress_is_reported_for_every_tier_in_order()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(12, 500));
        var progress = new ProgressRecorder<ProgressStep>();

        await _f.Pipeline.SummariseChapterAsync(ctx, 0, progress);

        progress.Steps.Select(s => s.Stage).Distinct()
            .Should().Equal("chunks", "sections", "final");
    }

    /// <summary>
    /// Changing book type must cost nothing. The artifacts are keyed by lens, so
    /// the old reading survives and the new one starts empty.
    /// </summary>
    [Fact]
    public async Task Switching_book_type_triggers_no_calls_and_keeps_the_old_reading()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));
        _f.Ai.Answer = _ => "the literary reading";
        await _f.Pipeline.SummariseChapterAsync(ctx, 0);

        var callsBefore = _f.Ai.Calls.Count;
        await _f.Store.Books.SetLensAsync(ctx.Ref, TestLens.LensKey);
        var switched = PipelineFixture.Context((await _f.Store.Books.GetAsync(ctx.Ref))!, new TestLens());

        _f.Ai.Calls.Should().HaveCount(callsBefore, "a type switch generates nothing");

        _f.Ai.Answer = _ => "the test-lens reading";
        (await _f.Pipeline.SummariseChapterAsync(switched, 0)).Markdown.Should().Be("the test-lens reading");
        (await _f.Pipeline.SummariseChapterAsync(ctx, 0)).Markdown.Should().Be("the literary reading");
    }

    [Fact]
    public async Task Explaining_simply_reuses_the_chapter_summary_rather_than_the_chapter()
    {
        var ctx = await _f.WithChapterAsync(PipelineFixture.Paragraphs(2, 100));
        _f.Ai.Answer = call => $"answer to {call.Endpoint}";

        await _f.Pipeline.SummariseChapterAsync(ctx, 0);
        var callsAfterSummary = _f.Ai.Calls.Count;

        await _f.Pipeline.ExplainSimplyAsync(ctx, 0);

        _f.Ai.Calls.Should().HaveCount(callsAfterSummary + 1, "the summary was already there");
        _f.Ai.Calls[^1].UserPrompt.Should()
            .Be("answer to reader2-chapter-summary", "it explains the summary, not the chapter");
    }
}
