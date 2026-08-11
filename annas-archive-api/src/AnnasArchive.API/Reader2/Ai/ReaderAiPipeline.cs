using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.API.Reader2.Ai;

/// <inheritdoc cref="IReaderAiPipeline" />
public sealed class ReaderAiPipeline(
    ArtifactGateway gateway,
    IArtifactStore artifacts,
    ChapterTextStore text,
    ModelCalls model,
    StoryModelService story,
    Reader2Options options) : IReaderAiPipeline
{
    public Task<ChapterLayout> LayoutAsync(ReaderContext ctx, int chapter, CancellationToken ct = default) =>
        gateway.GetOrComputeAsync(
            ArtifactKey.ChunkBoundaries(ctx.Ref, chapter),
            ctx,
            async token => ChapterLayout.For(
                await ReadChapterAsync(ctx, chapter, token),
                options.ChunkSize,
                options.ChunksPerSection),
            ct: ct);

    public Task<Prose> AnalysePassageAsync(
        ReaderContext ctx, PassageRequest request, bool force = false, CancellationToken ct = default) =>
        Generate(
            ArtifactKey.PassageAnalysis(ctx.Ref, ctx.Lens.Key, request.Chapter, request.WordOffset),
            ctx, CallKind.PassageAnalysis, force, ct,
            async token => Compose(
                await EarlierAnalysesAsync(ctx, request, token),
                ("Passage", request.Text)));

    public Task<Prose> SummariseSectionAsync(
        ReaderContext ctx, int chapter, int section, bool force = false, CancellationToken ct = default) =>
        Generate(
            ArtifactKey.SectionSummary(ctx.Ref, ctx.Lens.Key, chapter, section),
            ctx, CallKind.SectionSummary, force, ct,
            async token =>
            {
                var layout = await LayoutAsync(ctx, chapter, token);

                if (section < 0 || section >= layout.Sections.Count)
                    throw new ReaderAiException($"This chapter has no section {section + 1}.");

                var body = await ReadChapterAsync(ctx, chapter, token);
                var bounds = layout.Sections[section];

                return EpubTextExtractor.Slice(body, bounds.Start, bounds.WordCount);
            });

    public Task<Prose> ExplainSimplyAsync(
        ReaderContext ctx, int chapter, bool force = false, CancellationToken ct = default) =>
        Generate(
            ArtifactKey.ExplainSimply(ctx.Ref, ctx.Lens.Key, chapter),
            ctx, CallKind.ExplainSimply, force, ct,
            // Written from the summary, not the chapter. A second pass over the raw
            // text would produce a second summary; a pass over the first one is what
            // makes this read as somebody talking.
            async token => (await SummariseChapterAsync(ctx, chapter, ct: token)).Markdown);

    public Task<Prose> SummariseChapterAsync(
        ReaderContext ctx, int chapter, IProgress<ProgressStep>? progress = null,
        bool force = false, CancellationToken ct = default) =>
        gateway.GetOrGenerateAsync(
            ArtifactKey.ChapterSummary(ctx.Ref, ctx.Lens.Key, chapter),
            ctx, ctx.Lens.Versions[CallKind.ChapterSummary],
            token => ClimbLadderAsync(ctx, chapter, progress, token),
            force, ct);

    /// <summary>
    /// Tier 1 per chunk → tier 2 per group → tier 3 once, or a single call when
    /// the chapter is short.
    /// </summary>
    private async Task<Produced<Prose>> ClimbLadderAsync(
        ReaderContext ctx, int chapter, IProgress<ProgressStep>? progress, CancellationToken ct)
    {
        var body = await ReadChapterAsync(ctx, chapter, ct);
        var words = EpubTextExtractor.CountWords(body);

        // A 200-word interstitial chapter — and a long novel has many — otherwise
        // costs three calls and yields more summary than it had text.
        if (words < options.DirectSummaryWordThreshold)
        {
            progress?.Report(new ProgressStep("summarising", 1, 1, "Summarising a short chapter…"));
            return await model.AskLensAsync(
                ctx, CallKind.ChapterSummary, await WithStoryAsync(ctx, chapter, body, ct), ct);
        }

        var layout = await LayoutAsync(ctx, chapter, ct);

        var chunks = await LadderStepAsync(
            ctx, CallKind.ChunkSummary, "chunks", progress, ct,
            layout.Chunks.Select(c => EpubTextExtractor.Slice(body, c.Start, c.WordCount)).ToArray());

        var sections = await LadderStepAsync(
            ctx, CallKind.SectionSynthesis, "sections", progress, ct,
            chunks.Chunk(options.ChunksPerSection).Select(Join).ToArray());

        progress?.Report(new ProgressStep("final", 1, 1, "Writing the chapter summary…"));

        return await model.AskLensAsync(
            ctx, CallKind.ChapterSummary, await WithStoryAsync(ctx, chapter, Join(sections), ct), ct);
    }

    /// <summary>
    /// Prepends the story-so-far block to the tier-3 material, for a book type
    /// that keeps one (spec Phase 9).
    ///
    /// <para>Only the final call sees it: the lower rungs record what is in front
    /// of them, and the headings that need the record — who has been absent, what
    /// runs in parallel — are written at tier 3. Both tier-3 paths come through
    /// here, which includes the summary "I'm a Dummy" builds from. Reading the
    /// record is free and it arrives already filtered to this chapter, so a
    /// summary can remind but never spoil.</para>
    /// </summary>
    private async Task<string> WithStoryAsync(
        ReaderContext ctx, int chapter, string material, CancellationToken ct)
    {
        if (!ctx.Lens.BuildsStoryModel || ctx.Lens.StoryVocabulary is null) return material;

        var record = StoryContext.Build(
            await story.ReadThroughAsync(ctx, chapter, ct),
            ctx.Lens.StoryVocabulary, chapter,
            options.StoryDigestMaxActors, options.StoryDigestRecentChapters);

        return record is null
            ? material
            : $"{record}\n\n## This chapter\n\n{material}";
    }

    /// <summary>One tier: the same call over each input, reported as it goes.</summary>
    private async Task<string[]> LadderStepAsync(
        ReaderContext ctx, CallKind kind, string stage, IProgress<ProgressStep>? progress,
        CancellationToken ct, IReadOnlyList<string> inputs)
    {
        var outputs = new string[inputs.Count];

        for (var i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressStep(stage, i + 1, inputs.Count, $"{stage} {i + 1} of {inputs.Count}…"));

            outputs[i] = (await model.AskLensAsync(ctx, kind, inputs[i], ct)).Content.Markdown;
        }

        return outputs;
    }

    /// <summary>Shared shape for the one-call generators.</summary>
    private Task<Prose> Generate(
        ArtifactKey key, ReaderContext ctx, CallKind kind, bool force, CancellationToken ct,
        Func<CancellationToken, Task<string>> buildInput) =>
        gateway.GetOrGenerateAsync(
            key, ctx, ctx.Lens.Versions[kind],
            async token => await model.AskLensAsync(ctx, kind, await buildInput(token), token),
            force, ct);

    /// <summary>
    /// Analyses already written for earlier passages of this chapter, so the model
    /// does not re-explain what it explained two paragraphs ago.
    ///
    /// <para>Read from the store by key range. Reader I globbed a directory and
    /// parsed word offsets back out of filenames.</para>
    /// </summary>
    private async Task<string> EarlierAnalysesAsync(
        ReaderContext ctx, PassageRequest request, CancellationToken ct)
    {
        var earlier = await artifacts.ListAsync<Prose>(
            new ArtifactQuery(ctx.Ref, ctx.Lens.Key, ArtifactKind.PassageAnalysis, request.Chapter),
            new ArtifactVersions(Prose.SchemaVersion, ctx.Lens.Versions[CallKind.PassageAnalysis]), ct);

        return Join(earlier
            .Where(a => a.Key.Ordinal < request.WordOffset)
            .Select(a => a.Content.Markdown)
            .ToArray());
    }

    private async Task<string> ReadChapterAsync(ReaderContext ctx, int chapter, CancellationToken ct)
    {
        if (!text.HasChapter(ctx.Ref, chapter))
            throw new ReaderAiException("This chapter has not been extracted yet.");

        return await text.ReadChapterAsync(ctx.Ref, chapter, ct);
    }

    private static string Join(IReadOnlyList<string> parts) => string.Join("\n\n", parts);

    /// <summary>Labelled blocks, so the model can tell continuity from the passage itself.</summary>
    private static string Compose(string earlier, params (string Label, string Body)[] blocks)
    {
        var sections = blocks.Select(b => $"## {b.Label}\n\n{b.Body}");

        return earlier.Length == 0
            ? Join(sections.ToArray())
            : Join([$"## Already explained earlier in this chapter\n\n{earlier}", .. sections]);
    }
}
