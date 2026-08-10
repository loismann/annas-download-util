using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Storage;
using AnnasArchive.API.Reader2.Story;
using Microsoft.AspNetCore.Mvc;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// The story model: reading it, adding a chapter to it, and answering its
/// questions.
///
/// <para><b>Every response here is filtered to where the reader has got to, and
/// there is no way to ask for more.</b> When no chapter is given the filter falls
/// back to the reader's own stored position rather than to "everything" — a
/// default of everything would make the spoiler filter opt-in, and the one client
/// that forgot the parameter would hand somebody the end of the book.</para>
/// </summary>
internal static class StoryRoutes
{
    public static RouteGroupBuilder MapStoryRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/books/{bookId}/story-model", HandleRead);
        group.MapPost("/books/{bookId}/story-model/ingest", HandleIngest);
        group.MapPost("/books/{bookId}/story-model/back-fill", HandleBackFill);
        group.MapPost("/books/{bookId}/story-model/merges/{mergeId}/resolve", HandleResolve);

        return group;
    }

    /// <summary>
    /// The model as it stood at <c>throughChapter</c>. Free — reading what is
    /// already stored never reaches a model.
    /// </summary>
    private static Task<IResult> HandleRead(
        string bookId, int? throughChapter, HttpContext http, IReaderContextResolver resolver,
        StoryModelService story, ReaderStateStore state, CancellationToken ct) =>
        WithStoryLensAsync(bookId, http, resolver, ct, async ctx =>
        {
            var through = await ThroughAsync(ctx, state, throughChapter, ct);

            return Results.Ok(StoryModelResponse.From(
                await story.ReadThroughAsync(ctx, through, ct), ctx.Lens, through));
        });

    /// <summary>
    /// Folds one chapter in. A <c>POST</c> because it bills, and idempotent because
    /// the model records which chapters it has already seen.
    /// </summary>
    private static Task<IResult> HandleIngest(
        string bookId, [FromBody] IngestChapterRequest request, HttpContext http,
        IReaderContextResolver resolver, StoryModelService story, CancellationToken ct) =>
        WithStoryLensAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (request.Chapter < 0) return BadRequest("There is no such chapter.");

            var result = await story.IngestAsync(ctx, request.Chapter, ct);

            // The chapter just ingested is by definition one the reader has reached,
            // so it is the right horizon for the answer.
            return result.Skipped == IngestSkip.NoSummary
                ? BadRequest(
                    "That chapter has not been summarised yet. The story model is built from "
                    + "summaries, so summarise it first.")
                : Results.Ok(StoryModelResponse.From(
                    story.Through(result.Model, request.Chapter), ctx.Lens, request.Chapter));
        });

    /// <summary>
    /// Builds the model from every chapter already summarised.
    ///
    /// <para>Streamed, because a three-hundred-chapter novel is a long wait and a
    /// spinner with no counter reads as a hang. Never runs on its own — switching a
    /// book's type offers this, and a reader presses it.</para>
    /// </summary>
    private static Task<IResult> HandleBackFill(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        StoryModelService story, BookIngestor ingestor, ReaderStateStore state,
        ArtifactGateway gateway, CancellationToken ct) =>
        ReaderRequest.StreamingAsync(bookId, http, resolver, gateway, spends: true, ct, async (ctx, stream) =>
        {
            if (!ctx.Lens.BuildsStoryModel)
                throw new ReaderAiException($"{ctx.Lens.DisplayName} books do not build a story model.");

            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                throw new ReaderAiException("This book has not been extracted yet.");

            var model = await story.BackFillAsync(ctx, index.Chapters.Count, stream.Progress, ct);
            var through = await ThroughAsync(ctx, state, null, ct);

            await stream.ResultAsync(StoryModelResponse.From(story.Through(model, through), ctx.Lens, through));
        });

    /// <summary>
    /// Answers one candidate merge. The only way an actor is ever removed, which
    /// is why it takes an explicit yes rather than inferring one from a click.
    /// </summary>
    private static Task<IResult> HandleResolve(
        string bookId, string mergeId, [FromBody] ResolveMergeRequest request, HttpContext http,
        IReaderContextResolver resolver, StoryModelService story, ReaderStateStore state,
        CancellationToken ct) =>
        WithStoryLensAsync(bookId, http, resolver, ct, async ctx =>
        {
            var model = await story.ResolveAsync(ctx, mergeId, request.Accept, ct);
            var through = await ThroughAsync(ctx, state, null, ct);

            return Results.Ok(StoryModelResponse.From(story.Through(model, through), ctx.Lens, through));
        });

    /// <summary>
    /// How far the answer may look. An explicit request wins; otherwise it is
    /// wherever this reader had got to, and zero for a reader who has not started.
    /// </summary>
    private static async Task<int> ThroughAsync(
        ReaderContext ctx, ReaderStateStore state, int? requested, CancellationToken ct) =>
        requested ?? (await state.GetPositionAsync(ctx.Ref, ctx.UserId, ct))?.Chapter ?? 0;

    /// <summary>
    /// As <see cref="ReaderRequest.WithContextAsync"/>, plus the one check all
    /// three non-streaming routes share.
    /// </summary>
    private static Task<IResult> WithStoryLensAsync(
        string bookId, HttpContext http, IReaderContextResolver resolver, CancellationToken ct,
        Func<ReaderContext, Task<IResult>> body) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, ctx => ctx.Lens.BuildsStoryModel
            ? body(ctx)

            // A 409 rather than a 404: the book exists and so does the route, but
            // this book's type has no cast to keep. "Not found" would send a client
            // looking for a typo.
            : Task.FromResult(Problem(409, $"{ctx.Lens.DisplayName} books do not build a story model.")));
}
