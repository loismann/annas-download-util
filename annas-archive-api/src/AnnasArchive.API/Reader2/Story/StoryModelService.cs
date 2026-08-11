using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Storage;
using Serilog;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>Why one chapter's ingest did nothing, or null when it did something.</summary>
public enum IngestSkip
{
    /// <summary>Already folded in. The idempotency that makes a back-fill resumable.</summary>
    AlreadyIngested,

    /// <summary>No chapter summary to extract from. Ingest never summarises.</summary>
    NoSummary,

    /// <summary>This book's type does not accumulate a story model.</summary>
    NotAStoryLens
}

public sealed record IngestResult(StoryModel Model, IngestSkip? Skipped)
{
    public bool DidWork => Skipped is null;
}

/// <summary>
/// Reads, ingests, and back-fills a book's story model.
///
/// <para><b>The story model is state, not output.</b> Every other artifact is
/// regenerated when its prompt changes; this one is accumulated over hundreds of
/// chapters and could never be rebuilt for free, so it is read <i>without</i> the
/// prompt-version gate. A wording change must not empty somebody's cast list.
/// Provenance still records the version that wrote it, so an explicit rebuild
/// remains possible later.</para>
///
/// <para><b>The lost-update trap.</b> The whole model is one artifact row per
/// (book, lens), so two chapters ingesting at once would both read it, both
/// merge, and the second write would erase the first. Every ingest goes through
/// <see cref="ArtifactGateway.GetOrGenerateAsync"/> on the same key, and its keyed
/// lock is what serialises them. That is load-bearing rather than incidental,
/// which is why it is written here and tested rather than left to be
/// rediscovered.</para>
/// </summary>
public sealed class StoryModelService(
    ArtifactGateway gateway,
    IArtifactStore artifacts,
    ModelCalls model,
    Reader2Options options,
    CastOverrideStore corrections)
{
    /// <summary>What the reader has corrected. Owned by <see cref="CastOverrideStore"/>.</summary>
    public Task<CastOverrides> CorrectionsAsync(ReaderContext ctx, CancellationToken ct = default) =>
        corrections.ReadAsync(ctx, ct);

    /// <summary>
    /// The stored model with the reader's corrections laid over it.
    ///
    /// <para><b>Everything anybody reads goes through here; the merge does not.</b>
    /// <see cref="ReadAsync"/> stays raw on purpose — merging into a corrected model
    /// would write the correction back into storage on the next chapter, and a
    /// projection that quietly becomes permanent is not a projection.</para>
    /// </summary>
    public async Task<StoryModel> ReadCorrectedAsync(
        ReaderContext ctx, CancellationToken ct = default) =>
        CastCorrections.Apply(await ReadAsync(ctx, ct), await corrections.ReadAsync(ctx, ct));

    /// <summary>Saves one correction and returns the model as it now reads.</summary>
    public async Task<StoryModel> CorrectAsync(
        ReaderContext ctx, CastOverride correction, CancellationToken ct = default) =>
        CastCorrections.Apply(
            await ReadAsync(ctx, ct), await corrections.SaveAsync(ctx, correction, ct));

    /// <summary>Keeps somebody off the map, or puts them back.</summary>
    public async Task<StoryModel> HideAsync(
        ReaderContext ctx, string nameKey, bool hidden, CancellationToken ct = default) =>
        CastCorrections.Apply(
            await ReadAsync(ctx, ct), await corrections.HideAsync(ctx, nameKey, hidden, ct));

    /// <summary>
    /// The stored model, unfiltered. Read with no prompt gate — see the note on
    /// this class, and note that the zero is the whole of that decision.
    /// </summary>
    public async Task<StoryModel> ReadAsync(ReaderContext ctx, CancellationToken ct = default) =>
        (await artifacts.GetAsync<StoryModel>(
            ArtifactKey.StoryModel(ctx.Ref, ctx.Lens.Key),
            new ArtifactVersions(StoryModel.SchemaVersion, Prompt: 0), ct))?.Content
        ?? StoryModel.Empty;

    /// <summary>The model as it stood when the reader reached this chapter.</summary>
    public async Task<StoryModel> ReadThroughAsync(
        ReaderContext ctx, int throughChapter, CancellationToken ct = default) =>
        Through(await ReadCorrectedAsync(ctx, ct), throughChapter);

    /// <summary>
    /// The reading-position filter, applied under the configured thresholds.
    ///
    /// <para>Here rather than on the routes so that no endpoint reads configuration
    /// to work out what a reader may see. The filter recomputes thread dormancy,
    /// which needs the same threshold the merge sweeps by.</para>
    /// </summary>
    public StoryModel Through(StoryModel model, int throughChapter) =>
        model.ThroughChapter(throughChapter, options.MergeRules);

    /// <summary>
    /// Folds one chapter into the model, calling the extraction once.
    ///
    /// <para>Costs one fast-model call over prose that is <i>already</i> a summary,
    /// which is what keeps the per-chapter cost of the whole feature marginal.
    /// Never summarises: a chapter with no summary is skipped, because ingesting
    /// one would quietly turn a cheap action into an expensive one.</para>
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        ReaderContext ctx, int chapter, CancellationToken ct = default)
    {
        if (!ctx.Lens.BuildsStoryModel)
            return new IngestResult(StoryModel.Empty, IngestSkip.NotAStoryLens);

        var current = await ReadAsync(ctx, ct);

        // Cheap pre-check. It avoids paying for an extraction that would be
        // discarded, but it is not what makes re-ingesting safe — the check inside
        // the lock below is, and the merger's own guard behind that.
        if (current.HasIngested(chapter)) return new IngestResult(current, IngestSkip.AlreadyIngested);

        if (await SummaryAsync(ctx, chapter, ct) is not { } summary)
            return new IngestResult(current, IngestSkip.NoSummary);

        IngestSkip? skipped = null;

        var merged = await gateway.GetOrGenerateAsync(
            ArtifactKey.StoryModel(ctx.Ref, ctx.Lens.Key),
            ctx, ctx.Lens.Versions[CallKind.StoryExtraction],
            async token =>
            {
                // Re-read under the lock. The pre-check above raced; this one did
                // not, and returning the model unchanged here is what makes two
                // concurrent ingests of one chapter cost one call.
                var latest = await ReadAsync(ctx, token);

                if (latest.HasIngested(chapter))
                {
                    skipped = IngestSkip.AlreadyIngested;
                    return new Produced<StoryModel>(latest, Model: "none");
                }

                return await ExtractAsync(ctx, latest, chapter, summary, token);
            },
            force: true, ct);

        return new IngestResult(merged, skipped);
    }

    /// <summary>
    /// Empties the model, so that a back-fill behind it rebuilds from scratch.
    ///
    /// <para><b>For when the extraction contract itself changed</b>, not for a
    /// wording tweak. The model is read without a prompt-version gate on purpose —
    /// a rewording must never empty a cast list somebody spent a novel building —
    /// so when a change means the stored model was gathered under a contract that
    /// no longer holds, emptying it has to be something a person asks for.</para>
    ///
    /// <para>Answered merge questions do not survive this, and cannot: ids are
    /// assigned as actors are admitted, so a rebuilt model numbers everybody
    /// afresh and a refusal recorded against <c>a7</c> would come back attached to
    /// whoever <c>a7</c> now happens to be. Losing the answer is the honest
    /// outcome; re-pointing it at a stranger is not.</para>
    ///
    /// <para>Reaches no model, so it takes the gateway's un-gated path — an
    /// exhausted allowance must not leave somebody stuck with a model they have
    /// been told to rebuild.</para>
    /// </summary>
    public Task<StoryModel> ResetAsync(ReaderContext ctx, CancellationToken ct = default) =>
        gateway.ReviseAsync(
            ArtifactKey.StoryModel(ctx.Ref, ctx.Lens.Key), ctx.Lens.Versions[CallKind.StoryExtraction],
            _ => Task.FromResult(StoryModel.Empty), ct);

    /// <summary>
    /// Builds the model from every chapter already summarised, in order.
    ///
    /// <para>Offered after switching a book to a story-model type, and never run
    /// automatically. It is one extraction per chapter and no re-summarising, so
    /// it is the cheap half of the work — and it is resumable, because a chapter
    /// already in <c>chaptersIngested</c> costs nothing to walk past.</para>
    /// </summary>
    /// <param name="rebuild">
    /// Empties the model first. Every chapter is then re-extracted rather than
    /// walked past, which costs one call per summarised chapter — so it is a
    /// question the caller has to have answered, never a default.
    /// </param>
    public async Task<StoryModel> BackFillAsync(
        ReaderContext ctx, int chapterCount, IProgress<ProgressStep>? progress = null,
        bool rebuild = false, CancellationToken ct = default)
    {
        if (rebuild) await ResetAsync(ctx, ct);

        var model = await ReadAsync(ctx, ct);

        for (var chapter = 0; chapter < chapterCount; chapter++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressStep(
                "ingesting", chapter + 1, chapterCount, $"Chapter {chapter + 1} of {chapterCount}…"));

            var result = await IngestAsync(ctx, chapter, ct);
            model = result.Model;
        }

        return model;
    }

    /// <summary>
    /// Applies the reader's answer to one open question and stores the result.
    ///
    /// <para>Takes the same lock as an ingest, for the same reason: this is a
    /// read-modify-write of one row, and an ingest landing between the read and
    /// the write would lose whichever finished second. It reaches no model, so it
    /// goes through the gateway's un-gated path — a reader whose allowance is
    /// exhausted can still tidy a cast list they have already paid for.</para>
    /// </summary>
    public Task<StoryModel> ResolveAsync(
        ReaderContext ctx, string mergeId, bool accept, CancellationToken ct = default) =>
        gateway.ReviseAsync(
            ArtifactKey.StoryModel(ctx.Ref, ctx.Lens.Key), ctx.Lens.Versions[CallKind.StoryExtraction],
            async token => MergeResolution.Resolve(await ReadAsync(ctx, token), mergeId, accept),
            ct);

    /// <summary>One extraction call, parsed, merged. The only place a model is asked.</summary>
    private async Task<Produced<StoryModel>> ExtractAsync(
        ReaderContext ctx, StoryModel current, int chapter, string summary, CancellationToken ct)
    {
        // Corrected, so the digest offers the reader's preferred names and the
        // extraction starts using them — otherwise the same entry is corrected
        // forever. The merge below still folds into the raw model.
        var digest = StoryDigest.Build(
            CastCorrections.Apply(current, await CorrectionsAsync(ctx, ct)),
            chapter, options.StoryDigestMaxActors, options.StoryDigestRecentChapters);
        var produced = await model.AskLensAsync(ctx, CallKind.StoryExtraction, Compose(chapter, digest, summary), ct);

        // A model that answered with something unreadable has cost the household a
        // call either way, so the chapter is still marked ingested: charging twice
        // for the same unusable answer is the worse of the two failures.
        if (!StoryExtraction.TryParse(produced.Content.Markdown, chapter, out var delta))
            Log.Warning(
                "[reader2] Story extraction for {Book} chapter {Chapter} was not readable JSON",
                ctx.Ref, chapter);

        return new Produced<StoryModel>(
            StoryModelMerger.Merge(current, delta, options.MergeRules),
            produced.Model, produced.PromptTokens, produced.CompletionTokens);
    }

    /// <summary>The stored chapter summary, or null. Never generates one.</summary>
    private async Task<string?> SummaryAsync(ReaderContext ctx, int chapter, CancellationToken ct) =>
        (await gateway.PeekAsync<Prose>(
            ArtifactKey.ChapterSummary(ctx.Ref, ctx.Lens.Key, chapter),
            ctx.Lens.Versions[CallKind.ChapterSummary], ct))?.Markdown;

    private static string Compose(int chapter, string digest, string summary) =>
        $"""
         ## What the record already holds

         {digest}

         ## Chapter {chapter + 1} summary

         {summary}
         """;
}
