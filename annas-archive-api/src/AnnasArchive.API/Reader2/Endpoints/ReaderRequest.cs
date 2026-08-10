using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// Resolving the book and turning the four ways this can fail into responses —
/// once, for every route that takes a <c>bookId</c>.
///
/// <para>Without this each handler invents its own idea of what a missing book,
/// a retired lens, or an exhausted allowance means, and they drift. Reader I has
/// three endpoint files that answer the same failure three different ways.</para>
/// </summary>
internal static class ReaderRequest
{
    /// <summary>
    /// Runs <paramref name="body"/> with a resolved context, or answers instead.
    /// </summary>
    public static async Task<IResult> WithContextAsync(
        string bookId,
        HttpContext http,
        IReaderContextResolver resolver,
        CancellationToken ct,
        Func<ReaderContext, Task<IResult>> body)
    {
        if (!TryBookRef(bookId, out var book, out var badId)) return badId;

        var resolved = await resolver.ResolveAsync(book, http, ct);
        if (resolved.Context is null) return Describe(resolved.Failure!.Value);

        try
        {
            return await body(resolved.Context);
        }
        catch (TokenAllowanceException ex)
        {
            // The gate's own response — a 429 naming the allowance and when it resets.
            return ex.GateResponse;
        }
        catch (ReaderAiException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (EpubException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// As above, but for a stream.
    /// </summary>
    /// <param name="spends">
    /// Whether this route will definitely bill. When it will, the allowance is
    /// checked <b>before</b> a single header goes out — once the response is an
    /// event stream there is no way to answer 429, and a reader over their limit
    /// would get a 200 with an error frame buried in it.
    ///
    /// <para>False for ingestion, which is local work. It can still reach a model
    /// through chapter labelling, but that path tolerates its own failure and
    /// keeps the book's own titles, so refusing the whole ingest over an
    /// allowance would block something that costs nothing.</para>
    /// </param>
    public static Task<IResult> StreamingAsync(
        string bookId,
        HttpContext http,
        IReaderContextResolver resolver,
        ArtifactGateway gateway,
        bool spends,
        CancellationToken ct,
        Func<ReaderContext, SseStream, Task> body) =>
        WithContextAsync(bookId, http, resolver, ct, async ctx =>
        {
            if (spends && gateway.Refusal(ctx) is { } refusal) return refusal;

            ServerSentEventsHelper.BeginStream(http.Response);
            var stream = new SseStream(http.Response);

            try
            {
                await body(ctx, stream);
            }
            catch (OperationCanceledException)
            {
                // The reader navigated away. Nothing was persisted — the gateway
                // writes only once the work completes — so there is nobody to tell
                // and nothing to clean up.
                return Results.Empty;
            }
            catch (Exception ex) when (ex is ReaderAiException or EpubException or TokenAllowanceException)
            {
                // Exactly one error frame, after everything already queued. Two
                // would render two messages in the reader.
                await stream.ErrorAsync(ex.Message);
            }

            return Results.Empty;
        });

    private static IResult Describe(ReaderContextFailure failure) => failure switch
    {
        ReaderContextFailure.UnknownBook => NotFound("No such book."),
        ReaderContextFailure.NoUser => Results.Unauthorized(),
        ReaderContextFailure.UnknownLens => Problem(
            409,
            "This book is filed under a book type this build no longer has. "
            + "Change its type to read it; its existing work is kept."),
        _ => Problem(500, "This book could not be opened.")
    };
}
