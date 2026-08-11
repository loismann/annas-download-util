using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Vocabulary;
using Microsoft.AspNetCore.Mvc;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// Words the reader is working on, and the definitions behind them.
///
/// <para>The list routes are per-reader and book-independent — a term survives
/// un-enrolling the book it was met in. The generating routes are `POST`, like
/// every other route that spends.</para>
/// </summary>
internal static class VocabularyRoutes
{
    public static RouteGroupBuilder MapVocabularyRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/vocabulary", HandleList);
        group.MapPost("/vocabulary", HandleSave);
        group.MapDelete("/vocabulary/{term}", HandleRemove);
        group.MapDelete("/vocabulary", HandleClear);

        group.MapGet("/books/{bookId}/chapters/{chapter:int}/sections/{section:int}/vocabulary", HandlePeekSectionVocab);
        group.MapPost("/books/{bookId}/chapters/{chapter:int}/sections/{section:int}/vocabulary", HandleGenerateSectionVocab);
        group.MapPost("/books/{bookId}/chapters/{chapter:int}/vocabulary", HandleChapterVocab);
        group.MapPost("/books/{bookId}/vocabulary/learn-more", HandleLearnMore);
        group.MapDelete("/books/{bookId}/vocabulary", HandleForgetBook);

        group.MapGet("/books/{bookId}/flashcards", HandleFlashcards);
        group.MapPost("/books/{bookId}/flashcards", HandleAddFlashcard);
        group.MapDelete("/books/{bookId}/flashcards/{term}", HandleRemoveFlashcard);
        group.MapDelete("/books/{bookId}/flashcards", HandleClearFlashcards);

        return group;
    }

    // ─── the reader's own words ──────────────────────────────────────────

    private static Task<IResult> HandleList(
        string? state, HttpContext http, VocabularyStore vocabulary, CancellationToken ct) =>
        ForReader(http, async user =>
        {
            if (!TryState(state, out var parsed)) return BadRequest(UnknownState(state));

            return Results.Ok(await vocabulary.ListAsync(user, parsed, ct));
        });

    private static Task<IResult> HandleSave(
        [FromBody] SaveTermRequest request, HttpContext http,
        VocabularyStore vocabulary, CancellationToken ct) =>
        ForReader(http, async user =>
        {
            if (string.IsNullOrWhiteSpace(request.Term)) return BadRequest("A term is required.");
            if (!TryState(request.State, out var state) || state is null)
                return BadRequest(UnknownState(request.State));

            BookRef? book = BookRef.TryParse(request.BookId, out var parsed) ? parsed : null;

            // Saving a term that is already filed moves it, which is how
            // known↔studying works — one operation, not three.
            await vocabulary.SaveAsync(user, request.Term, state.Value, request.Definition, book, ct);

            return Results.NoContent();
        });

    private static Task<IResult> HandleRemove(
        string term, HttpContext http, VocabularyStore vocabulary, CancellationToken ct) =>
        ForReader(http, async user =>
            await vocabulary.RemoveAsync(user, term, ct)
                ? Results.NoContent()
                : NotFound($"'{term}' is not in your vocabulary."));

    /// <summary>Clears everything, or only the known or studying half.</summary>
    private static Task<IResult> HandleClear(
        string? state, HttpContext http, VocabularyStore vocabulary, CancellationToken ct) =>
        ForReader(http, async user =>
        {
            if (!TryState(state, out var parsed)) return BadRequest(UnknownState(state));

            return Results.Ok(new { removed = await vocabulary.ClearAsync(user, parsed, ct) });
        });

    // ─── definitions from the book ───────────────────────────────────────

    /// <summary>
    /// One section's hard words, as already stored. A <c>GET</c>, and free —
    /// separate from <see cref="HandleGenerateSectionVocab"/> rather than one
    /// handler serving both verbs, because a shared handler that could fall
    /// through to generating on a cache miss would spend money on a request a
    /// browser can prefetch and a refresh can repeat.
    /// </summary>
    private static Task<IResult> HandlePeekSectionVocab(
        string bookId, int chapter, int section, HttpContext http,
        IReaderContextResolver resolver, VocabularyPipeline vocabulary, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await vocabulary.PeekSectionAsync(ctx, chapter, section, ct)));

    private static Task<IResult> HandleGenerateSectionVocab(
        string bookId, int chapter, int section, bool? force, HttpContext http,
        IReaderContextResolver resolver, VocabularyPipeline vocabulary, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await vocabulary.ForSectionAsync(ctx, chapter, section, force is true, ct)));

    private static Task<IResult> HandleChapterVocab(
        string bookId, int chapter, bool? force, HttpContext http,
        IReaderContextResolver resolver, VocabularyPipeline vocabulary,
        ArtifactGateway gateway, CancellationToken ct) =>
        ReaderRequest.StreamingAsync(bookId, http, resolver, gateway, spends: true, ct,
            async (ctx, stream) => await stream.ResultAsync(
                await vocabulary.ForChapterAsync(ctx, chapter, stream.Progress, force is true, ct)));

    private static Task<IResult> HandleLearnMore(
        string bookId, [FromBody] LearnMoreRequest request, bool? force, HttpContext http,
        IReaderContextResolver resolver, VocabularyPipeline vocabulary, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await vocabulary.DeepDiveAsync(ctx, request.Term ?? "", request.Context, force is true, ct)));

    /// <summary>
    /// Drops this book's fingerprints from the reader's vocabulary without
    /// dropping the words. Un-enrolling should take the provenance, not the
    /// learning.
    /// </summary>
    private static Task<IResult> HandleForgetBook(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        VocabularyStore vocabulary, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(new { forgotten = await vocabulary.ForgetBookAsync(ctx.UserId, ctx.Ref, ct) }));

    // ─── flashcards ──────────────────────────────────────────────────────

    private static Task<IResult> HandleFlashcards(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        FlashcardStore cards, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await cards.ListAsync(ctx.Ref, ct)));

    private static Task<IResult> HandleAddFlashcard(
        string bookId, [FromBody] FlashcardRequest request, HttpContext http,
        IReaderContextResolver resolver, FlashcardStore cards, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (string.IsNullOrWhiteSpace(request.Term)) return BadRequest("A term is required.");

            return Results.Ok(await cards.AddAsync(ctx.Ref, request.Term, request.Definition ?? "", ct));
        });

    private static Task<IResult> HandleRemoveFlashcard(
        string bookId, string term, HttpContext http, IReaderContextResolver resolver,
        FlashcardStore cards, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await cards.RemoveAsync(ctx.Ref, term, ct)));

    private static Task<IResult> HandleClearFlashcards(
        string bookId, HttpContext http, IReaderContextResolver resolver,
        FlashcardStore cards, CancellationToken ct) =>
        ReaderRequest.WithContextAsync(bookId, http, resolver, ct, async ctx =>
            Results.Ok(await cards.ClearAsync(ctx.Ref, ct)));

    // ─── shared plumbing ─────────────────────────────────────────────────

    /// <summary>
    /// Vocabulary is per-reader and book-independent, so these routes need a user
    /// and nothing else — no book to resolve, and no lens.
    /// </summary>
    private static async Task<IResult> ForReader(HttpContext http, Func<string, Task<IResult>> body) =>
        UserHelpers.GetUserIdFromContext(http) is { Length: > 0 } user
            ? await body(user)
            : Results.Unauthorized();

    /// <summary>Null means "both"; anything unrecognised is refused, never ignored.</summary>
    private static bool TryState(string? value, out TermState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(value) || value == "all") return true;

        if (Enum.TryParse<TermState>(value, ignoreCase: true, out var parsed))
        {
            state = parsed;
            return true;
        }

        return false;
    }

    private static string UnknownState(string? value) =>
        $"'{value}' is not a vocabulary state. Known states: known, studying, all.";
}
