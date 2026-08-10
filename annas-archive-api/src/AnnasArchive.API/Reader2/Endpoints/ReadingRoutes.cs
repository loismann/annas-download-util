using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Export;
using AnnasArchive.API.Reader2.Storage;
using Microsoft.AspNetCore.Mvc;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// The per-reader routes — where somebody is in a book, how they like it to
/// look — plus taking a book's work away with them.
/// </summary>
internal static class ReadingRoutes
{
    public static RouteGroupBuilder MapReadingRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/books/{bookId}/position", HandleGetPosition);
        group.MapPut("/books/{bookId}/position", HandleSetPosition);
        group.MapGet("/books/{bookId}/bookmarks", HandleListBookmarks);
        group.MapPost("/books/{bookId}/bookmarks", HandleSaveBookmark);
        group.MapDelete("/books/{bookId}/bookmarks/{bookmarkId}", HandleRemoveBookmark);
        group.MapGet("/books/{bookId}/export", HandleExport);
        group.MapGet("/preferences", HandleGetPreferences);
        group.MapPut("/preferences", HandleSetPreferences);

        return group;
    }

    private static Task<IResult> HandleGetPosition(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        ReaderStateStore state, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await state.GetPositionAsync(ctx.Ref, ctx.UserId, ct)
                       ?? new ReadingPosition(0, 0, DateTime.UtcNow)));

    private static Task<IResult> HandleSetPosition(
        string bookId, [FromBody] SetPositionRequest request, HttpContext http,
        IReaderContextResolver resolver, ReaderStateStore state, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (Negative(request.Chapter, request.WordOffset) is { } bad) return bad;

            await state.SetPositionAsync(ctx.Ref, ctx.UserId, request.Chapter, request.WordOffset, ct);
            return Results.NoContent();
        });

    private static Task<IResult> HandleListBookmarks(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        BookmarkStore bookmarks, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await bookmarks.ListAsync(ctx.Ref, ctx.UserId, ct)));

    private static Task<IResult> HandleSaveBookmark(
        string bookId, [FromBody] SaveBookmarkRequest request, HttpContext http,
        IReaderContextResolver resolver, BookmarkStore bookmarks, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (Negative(request.Chapter, request.WordOffset) is { } bad) return bad;

            var label = request.Label?.Trim();
            if (label is { Length: > MaxLabel })
                return BadRequest($"A bookmark label is at most {MaxLabel} characters.");

            return Results.Ok(await bookmarks.SaveAsync(
                ctx.Ref, ctx.UserId, request.Chapter, request.WordOffset,
                label is { Length: > 0 } ? label : null, ct));
        });

    private static Task<IResult> HandleRemoveBookmark(
        string bookId, string bookmarkId, HttpContext http, IReaderContextResolver resolver,
        BookmarkStore bookmarks, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            await bookmarks.RemoveAsync(ctx.Ref, ctx.UserId, bookmarkId, ct)
                ? Results.NoContent()
                : NotFound("No such bookmark."));

    private const int MaxLabel = 200;

    /// <summary>
    /// Shared by the two routes that take a place in a book, so a position and a
    /// bookmark cannot disagree about what a valid one is.
    /// </summary>
    private static IResult? Negative(int chapter, int wordOffset) =>
        chapter < 0 || wordOffset < 0
            ? BadRequest("A reading position cannot be negative.")
            : null;

    /// <summary>
    /// Everything generated for this book <i>under its current book type</i>, as
    /// one Markdown document.
    ///
    /// <para>Lens-scoped deliberately. A book read twice has two sets of work, and
    /// interleaving a military reading with a literary one produces a document
    /// that contradicts itself paragraph to paragraph.</para>
    /// </summary>
    private static Task<IResult> HandleExport(
        string bookId, string? format, HttpContext http, IReaderContextResolver resolver,
        BookIngestor ingestor, IArtifactStore artifacts, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (format is not (null or "md"))
                return BadRequest($"'{format}' is not an export format. Known formats: md.");

            if (await ingestor.CompleteIndexAsync(ctx.Ref, ct) is not { } index)
                return Problem(409, "This book has not been extracted yet.");

            var markdown = await ExportMarkdown.BuildAsync(ctx, index, artifacts, ct);

            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(markdown),
                "text/markdown",
                $"{FileNames.Sanitize(ctx.Book.Title)}-{ctx.Lens.Key}.md");
        });

    private static async Task<IResult> HandleGetPreferences(
        HttpContext http, ReaderStateStore state, CancellationToken ct) =>
        UserHelpers.GetUserIdFromContext(http) is { Length: > 0 } user
            ? Results.Ok(await state.GetPreferencesAsync(user, ct))
            : Results.Unauthorized();

    private static async Task<IResult> HandleSetPreferences(
        [FromBody] ReadingPreferences preferences, HttpContext http,
        ReaderStateStore state, CancellationToken ct)
    {
        if (UserHelpers.GetUserIdFromContext(http) is not { Length: > 0 } user)
            return Results.Unauthorized();

        if (preferences.FontSize is < 8 or > 48)
            return BadRequest("Font size must be between 8 and 48.");

        if (preferences.SplitRatio is < 0.1 or > 0.9)
            return BadRequest("The split must leave both panes usable.");

        await state.SetPreferencesAsync(user, preferences, ct);
        return Results.NoContent();
    }
}
