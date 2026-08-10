using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Storage;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>Reading a book: its structure, its text, and finding a word in it.</summary>
internal static class ChapterRoutes
{
    public static RouteGroupBuilder MapChapterRoutes(this RouteGroupBuilder group)
    {
        group.MapPost("/books/{bookId}/ingest", HandleIngest);
        group.MapGet("/books/{bookId}/chapters", HandleChapters);
        group.MapGet("/books/{bookId}/chapters/{chapter:int}", HandleChapter);
        group.MapGet("/books/{bookId}/chapters/{chapter:int}/sections", HandleSections);
        group.MapGet("/books/{bookId}/search", HandleSearch);
        group.MapDelete("/books/{bookId}/index", HandleDropIndex);

        return group;
    }

    /// <summary>
    /// Extracts the book, streaming progress. A <c>POST</c> because it does work,
    /// even though the work is local — and idempotent, so a reload is free.
    /// </summary>
    private static Task<IResult> HandleIngest(
        string bookId, bool? force, HttpContext http, IReaderContextResolver resolver,
        BookIngestor ingestor, IBookRegistry books, ChapterLabeller labeller,
        ArtifactGateway gateway, IArtifactStore artifacts, CancellationToken ct) =>
        ReaderRequest.StreamingAsync(bookId, http, resolver, gateway, spends: false, ct, async (ctx, stream) =>
        {
            var extracted = await ingestor.IngestAsync(ctx.Book, stream.Progress, force is true, ct);

            // The one AI call a reader did not click for, and it is shown as a step
            // rather than hidden. Off by configuration, and a failure here keeps the
            // book's own titles rather than failing the ingest.
            stream.Progress.Report(new ProgressStep("labelling", 1, 1, "Tidying chapter titles…"));
            var index = await labeller.ApplyAsync(ctx, extracted, ct);

            await books.TouchOpenedAsync(ctx.Ref, ct);
            await stream.ResultAsync(new ChapterListResponse(
                index.Title, ctx.Lens.Key,
                ChapterInfo.ForList(index.Chapters, await SummarisedAsync(ctx, artifacts, ct))));
        });

    private static Task<IResult> HandleChapters(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        BookIngestor ingestor, IBookRegistry books, ChapterLabeller labeller,
        IArtifactStore artifacts, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                return NotIngested();

            await books.TouchOpenedAsync(ctx.Ref, ct);

            // Applies labels already stored. It never generates them — that would
            // make opening a book cost money, which is the rule this reader keeps.
            var labelled = await labeller.StoredLabelsAsync(ctx, index, ct);

            return Results.Ok(new ChapterListResponse(
                labelled.Title, ctx.Lens.Key,
                ChapterInfo.ForList(labelled.Chapters, await SummarisedAsync(ctx, artifacts, ct))));
        });

    /// <summary>
    /// Which chapters already have a summary under this lens.
    ///
    /// <para>One read for the whole book, and it applies the same version gates a
    /// generate would: a summary written by wording nobody uses any more is a
    /// cache miss, so showing it as already bought would promise the reader
    /// something the next click would charge them for.</para>
    /// </summary>
    private static async Task<IReadOnlySet<int>> SummarisedAsync(
        ReaderContext ctx, IArtifactStore artifacts, CancellationToken ct) =>
        (await artifacts.ListAsync<Prose>(
            new ArtifactQuery(ctx.Ref, ctx.Lens.Key, ArtifactKind.ChapterSummary),
            new ArtifactVersions(Prose.SchemaVersion, ctx.Lens.PromptVersion), ct))
        .Select(a => a.Key.Chapter)
        .ToHashSet();

    private static Task<IResult> HandleChapter(
        string bookId, int chapter, HttpContext http, IReaderContextResolver resolver,
        BookIngestor ingestor, ChapterTextStore text, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                return NotIngested();

            if (index.Find(chapter) is not { } found)
                return NotFound($"This book has no chapter {chapter + 1}.");

            return Results.Ok(new ChapterResponse(
                ChapterInfo.From(found), await text.ReadChapterAsync(ctx.Ref, chapter, ct)));
        });

    /// <summary>
    /// Where the chapter's sections start and end. Computed and free — this route
    /// deliberately spends nothing, so opening a chapter never costs money.
    /// </summary>
    private static Task<IResult> HandleSections(
        string bookId, int chapter, HttpContext http, IReaderContextResolver resolver,
        IReaderAiPipeline pipeline, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            var layout = await pipeline.LayoutAsync(ctx, chapter, ct);

            return Results.Ok(layout.Sections
                .Select((s, i) => new SectionInfo(i, s.Start, s.WordCount))
                .ToArray());
        });

    private static Task<IResult> HandleSearch(
        string bookId, string? q, HttpContext http, IReaderContextResolver resolver,
        BookIngestor ingestor, ChapterTextStore text, Reader2Options options, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (BookSearch.Validate(q, options.SearchMinQueryLength, options.SearchMaxQueryLength) is { } refused)
                return BadRequest(refused.Reason);

            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                return NotIngested();

            return Results.Ok(BookSearch.Run(
                q!, index.Chapters, c => text.TryReadChapter(ctx.Ref, c.Id)));
        });

    /// <summary>
    /// Throws away the extracted text and keeps every artifact.
    ///
    /// <para>The escape hatch for an extraction that came out wrong — a summary
    /// somebody paid for is not the thing that went wrong, so it stays.</para>
    ///
    /// <para>Deleting the text is enough to un-ingest the book: the index counts
    /// as complete only while every chapter it names is on disk, so the next open
    /// re-extracts. No second mechanism, and nothing to keep in step.</para>
    /// </summary>
    private static Task<IResult> HandleDropIndex(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        ChapterTextStore text, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, ctx =>
        {
            text.Delete(ctx.Ref);
            return Task.FromResult(Results.NoContent());
        });

    private static IResult NotIngested() =>
        Problem(409, "This book has not been extracted yet. Open it to index it first.");
}
