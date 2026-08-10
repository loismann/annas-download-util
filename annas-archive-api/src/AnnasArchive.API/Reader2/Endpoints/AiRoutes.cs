using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Story;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// The routes that spend money.
///
/// <para>Every one is a <c>POST</c>, and that is a rule rather than a
/// convention: <b>nothing generates on open, on scroll, or ahead of the reader.</b>
/// A <c>GET</c> here would be a route a browser could prefetch, a crawler could
/// follow, and a refresh could re-bill. An architecture test checks the whole
/// group for it.</para>
///
/// <para><c>force=true</c> is accepted everywhere and means "ignore what is
/// stored and buy it again" — never "add a second row".</para>
/// </summary>
internal static class AiRoutes
{
    public static RouteGroupBuilder MapAiRoutes(this RouteGroupBuilder group)
    {
        group.MapPost("/books/{bookId}/chapters/{chapter:int}/summary", HandleChapterSummary);
        group.MapPost("/books/{bookId}/chapters/{chapter:int}/sections/{section:int}/summary", HandleSectionSummary);
        group.MapPost("/books/{bookId}/chapters/{chapter:int}/explain-simply", HandleExplainSimply);
        group.MapPost("/books/{bookId}/passage-analysis", HandlePassageAnalysis);

        return group;
    }

    /// <summary>
    /// The three-tier ladder, streamed. The stream exists because this is the one
    /// call that can take a minute, and a reader watching a spinner with no
    /// counter assumes it has hung.
    /// </summary>
    private static Task<IResult> HandleChapterSummary(
        string bookId, int chapter, bool? force, HttpContext http,
        IReaderContextResolver resolver, IReaderAiPipeline pipeline, ArtifactGateway gateway,
        StoryModelService story, Reader2Options options, CancellationToken ct) =>
        ReaderRequest.StreamingAsync(bookId, http, resolver, gateway, spends: true, ct, async (ctx, stream) =>
        {
            var summary = await pipeline.SummariseChapterAsync(
                ctx, chapter, stream.Progress, force is true, ct);

            await IngestQuietlyAsync(ctx, chapter, story, options, stream, ct);
            await stream.ResultAsync(summary);
        });

    /// <summary>
    /// Folds the chapter just summarised into the story model.
    ///
    /// <para>The one place work rides on a request the reader made for something
    /// else, so it is <b>a visible step rather than a hidden call</b>: it costs one
    /// fast-model call over prose already paid for, it is announced in the stream,
    /// and <c>Reader2:StoryModel:AutoIngestOnSummary</c> turns it off. With it off
    /// nothing here reaches a model at all — the gate is around the call, not
    /// around the reporting of it.</para>
    ///
    /// <para>A failure is swallowed. The reader asked for a summary and has one;
    /// losing it to a problem with a feature they did not ask about would be the
    /// wrong trade, and the chapter can be ingested again later.</para>
    /// </summary>
    private static async Task IngestQuietlyAsync(
        ReaderContext ctx, int chapter, StoryModelService story, Reader2Options options,
        SseStream stream, CancellationToken ct)
    {
        if (!options.AutoIngestOnSummary || !ctx.Lens.BuildsStoryModel) return;

        stream.Progress.Report(new ProgressStep("story", 1, 1, "Adding this chapter to the story model…"));

        try
        {
            await story.IngestAsync(ctx, chapter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[reader2] Story ingest for {Book} chapter {Chapter} failed", ctx.Ref, chapter);
        }
    }

    private static Task<IResult> HandleSectionSummary(
        string bookId, int chapter, int section, bool? force, HttpContext http,
        IReaderContextResolver resolver, IReaderAiPipeline pipeline, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await pipeline.SummariseSectionAsync(ctx, chapter, section, force is true, ct)));

    private static Task<IResult> HandleExplainSimply(
        string bookId, int chapter, bool? force, HttpContext http,
        IReaderContextResolver resolver, IReaderAiPipeline pipeline, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await pipeline.ExplainSimplyAsync(ctx, chapter, force is true, ct)));

    private static Task<IResult> HandlePassageAnalysis(
        string bookId, [FromBody] PassageAnalysisRequest request, bool? force, HttpContext http,
        IReaderContextResolver resolver, IReaderAiPipeline pipeline, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return BadRequest("Select some text to analyse.");

            if (request.WordOffset < 0)
                return BadRequest("A passage's word offset cannot be negative.");

            var analysis = await pipeline.AnalysePassageAsync(
                ctx,
                new PassageRequest(request.Chapter, request.WordOffset, request.Text.Trim()),
                force is true, ct);

            return Results.Ok(analysis);
        });
}
