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
        group.MapPut("/books/{bookId}/story-model/actors/{actorId}", HandleCorrect);
        group.MapPut("/books/{bookId}/story-model/actors/{actorId}/hidden", HandleHide);

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
    ///
    /// <para><c>rebuild=true</c> empties the model first, for when the extraction
    /// contract has changed under a model already gathered. It re-extracts every
    /// summarised chapter instead of walking past them, so it is opt-in.</para>
    /// </summary>
    private static Task<IResult> HandleBackFill(
        string bookId, bool? rebuild, HttpContext http, IReaderContextResolver resolver,
        StoryModelService story, BookIngestor ingestor, ReaderStateStore state,
        ArtifactGateway gateway, CancellationToken ct) =>
        ReaderRequest.StreamingAsync(bookId, http, resolver, gateway, spends: true, ct, async (ctx, stream) =>
        {
            if (!ctx.Lens.BuildsStoryModel)
                throw new ReaderAiException($"{ctx.Lens.DisplayName} books do not build a story model.");

            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                throw new ReaderAiException("This book has not been extracted yet.");

            var model = await story.BackFillAsync(
                ctx, index.Chapters.Count, stream.Progress, rebuild is true, ct);
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
    /// The reader's own correction to one entry: what to call them, a note of
    /// their own, and which other entries are the same person.
    /// </summary>
    /// <remarks>
    /// A <c>PUT</c> carrying the whole correction rather than three routes: it is
    /// one row about one person, and an idempotent replace has no ordering
    /// problem between a rename and a merge arriving at once. Sending it empty
    /// clears the correction, so there is no fourth route for undoing either.
    ///
    /// <para>Free. Nothing here reaches a model, and a reader out of allowance
    /// must still be able to fix a name.</para>
    /// </remarks>
    private static Task<IResult> HandleCorrect(
        string bookId, string actorId, [FromBody] CorrectActorRequest request, HttpContext http,
        IReaderContextResolver resolver, StoryModelService story, ReaderStateStore state,
        CancellationToken ct) =>
        WithStoryLensAsync(bookId, http, resolver, ct, async ctx =>
        {
            // Keyed by name, resolved from the id the client is looking at. The
            // client holds an id because that is what it was served; the store
            // holds a name because ids do not survive a rebuild.
            var model = await story.ReadAsync(ctx, ct);

            if (model.Actors.FirstOrDefault(a => a.Id == actorId) is not { } actor)
                return Problem(404, "There is nobody by that id in this book's record.");

            var sameAs = (request.SameAs ?? [])
                .Select(id => model.Actors.FirstOrDefault(a => a.Id == id))
                .OfType<Story.Actor>()
                .Where(a => a.Id != actor.Id)
                .Select(a => NameMatch.Key(a.CanonicalName))
                .ToArray();

            var corrected = await story.CorrectAsync(
                ctx,
                new CastOverride(
                    NameMatch.Key(actor.CanonicalName),
                    request.PreferredName, request.Note, sameAs),
                ct);

            var through = await ThroughAsync(ctx, state, null, ct);

            return Results.Ok(StoryModelResponse.From(story.Through(corrected, through), ctx.Lens, through));
        });

    /// <summary>
    /// Keeps somebody off the map, or puts them back.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="HandleCorrect"/>, which replaces a correction whole.
    /// Hiding is one press from a panel and the client cannot resend the rest of
    /// the correction — a preferred name is projected onto the canonical one, so
    /// nothing it was served tells it which is which. This merges instead.
    ///
    /// <para>Free, like every other correction.</para>
    /// </remarks>
    private static Task<IResult> HandleHide(
        string bookId, string actorId, [FromBody] HideActorRequest request, HttpContext http,
        IReaderContextResolver resolver, StoryModelService story, ReaderStateStore state,
        CancellationToken ct) =>
        WithStoryLensAsync(bookId, http, resolver, ct, async ctx =>
        {
            // Against the raw model, exactly as HandleCorrect does, because the
            // key has to be the same one both routes write under. Reading the
            // corrected model instead keys a renamed person under their new name
            // and leaves two rows for one character, whereupon whichever is
            // applied second wins and the other silently stops meaning anything.
            var model = await story.ReadAsync(ctx, ct);

            if (model.Actors.FirstOrDefault(a => a.Id == actorId) is not { } actor)
                return Problem(404, "There is nobody by that id in this book's record.");

            var hidden = await story.HideAsync(
                ctx, NameMatch.Key(actor.CanonicalName), request.Hidden, ct);

            var through = await ThroughAsync(ctx, state, null, ct);

            return Results.Ok(StoryModelResponse.From(story.Through(hidden, through), ctx.Lens, through));
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
