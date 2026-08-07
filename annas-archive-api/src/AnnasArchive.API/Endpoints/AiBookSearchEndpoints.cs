using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Services.BookDiscovery;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// The five AI book-discovery routes.
///
/// Each handler is now the decision sequence and nothing else — validate, pick a
/// model, ask, shape the answer. The prompt text lives in
/// <see cref="BookDiscoveryPrompts"/>, parsing in
/// <see cref="BookDiscoveryResponses"/>, the HTTP round trip and token
/// accounting in <see cref="IAiChatCompletion"/>, and the two enrichment passes
/// behind related-books in <see cref="IRelatedBooksEnricher"/>. What is left
/// here is the part that differs per route.
/// </summary>
public static class AiBookSearchEndpoints
{
    /// <summary>
    /// Applies the configured author-cache capacity. Called from
    /// ServiceConfiguration.ConfigureCaches at startup; kept on this class
    /// because that is where the call site expects it.
    /// </summary>
    public static void ConfigureCache(int capacity) => OpenLibraryAuthorLookup.ConfigureCache(capacity);

    public static WebApplication MapAiBookSearchEndpoints(this WebApplication app)
    {
        // POST /api/ai/suggest-authors - Suggest authors for a book title
        app.MapPost("/api/ai/suggest-authors", HandleSuggestAuthors)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/related-books - Find related books (series + other series by author)
        app.MapPost("/api/ai/related-books", HandleRelatedBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/book-search - AI book search (freeform query)
        app.MapPost("/api/ai/book-search", HandleBookSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/match-series-books - Match series books intelligently using GPT
        app.MapPost("/api/ai/match-series-books", HandleMatchSeriesBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/group-search-results - Detect which search results are
        // the same book (different format/duplicate upload) vs genuinely
        // different books, so the frontend can collapse duplicates into one card.
        app.MapPost("/api/ai/group-search-results", HandleGroupSearchResults)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleSuggestAuthors(
        HttpContext context,
        [FromBody] SuggestAuthorsRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BookTitle))
            return Results.BadRequest(new { error = "BookTitle is required." });

        try
        {
            // OpenLibrary first — it is free, cached, and right about most
            // titles. The header exists to force the comparison when the two
            // disagree.
            if (!HeaderIsTrue(context, "x-force-openai"))
            {
                var openLibraryAuthors = await OpenLibraryAuthorLookup.SuggestAsync(request.BookTitle, httpFactory);
                if (openLibraryAuthors.Count > 0)
                {
                    Log.Information("✅ Author suggestions (OpenLibrary) for '{BookTitle}': {AuthorCount} authors found",
                        request.BookTitle, openLibraryAuthors.Count);
                    return Results.Ok(new SuggestAuthorsResponse(openLibraryAuthors));
                }
            }

            if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit) return overLimit;

            var outcome = await chat.CompleteAsync(
                BookDiscoveryPrompts.SuggestAuthors(RequestedModel(context, modelSelection.GetModelFast()), request.BookTitle),
                context);
            if (!outcome.Succeeded) return outcome.Failure!;

            var authors = BookDiscoveryResponses.AuthorSuggestions(outcome.Text, aiResponseParser);
            Log.Information("✅ Author suggestions for '{BookTitle}': {AuthorCount} authors found",
                request.BookTitle, authors.Count);
            return Results.Ok(new SuggestAuthorsResponse(authors));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for suggest-authors: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ OpenAI suggest-authors failed: {ErrorMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to suggest authors.");
        }
    }

    private static async Task<IResult> HandleRelatedBooks(
        HttpContext context,
        [FromBody] RelatedBooksRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat,
        IRelatedBooksEnricher enricher)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BookTitle) || string.IsNullOrWhiteSpace(request.Author))
            return Results.BadRequest(new { error = "BookTitle and Author are required." });

        if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit) return overLimit;

        try
        {
            var model = modelSelection.GetModelFast();

            var outcome = await chat.CompleteAsync(
                BookDiscoveryPrompts.RelatedBooks(model, request.BookTitle, request.Author),
                context);
            if (!outcome.Succeeded) return outcome.Failure!;

            var payload = BookDiscoveryResponses.RelatedBooks(outcome.Text, aiResponseParser);

            var sameSeries = await enricher.ExpandSameSeriesAsync(payload.SameSeries, request, payload);
            var (filledSameSeries, filledOtherSeries) = await enricher.FillDescriptionsAsync(
                sameSeries, payload.OtherSeries, request.Author, model,
                UserHelpers.GetUserIdFromContext(context));

            Log.Information("✅ Related books for '{RequestBookTitle}': {SameSeriesCount} series books, {OtherSeriesCount} other series",
                request.BookTitle, filledSameSeries.Count, filledOtherSeries.Count);

            return Results.Ok(new RelatedBooksResponse(filledSameSeries, filledOtherSeries, payload.SeriesSummary));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for related-books: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ OpenAI related-books failed: {ExMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to get related books.");
        }
    }

    private static async Task<IResult> HandleBookSearch(
        HttpContext context,
        [FromBody] AiBookSearchRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required." });

        if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit) return overLimit;

        try
        {
            // A URL in the query means the person is pointing at somebody
            // else's list ("every book on this page"), so the titles are read
            // off the page first and the model is only asked to identify them.
            var extractedTitles = BookDiscoveryPrompts.ContainsUrl(request.Query)
                ? await BookTitleExtractionHelpers.ExtractBookTitlesFromQueryAsync(request.Query, httpFactory, cancellationToken)
                : [];

            var outcome = await chat.CompleteAsync(
                BookDiscoveryPrompts.BookSearch(modelSelection.GetModelDeep(), request.Query, extractedTitles),
                context,
                cancellationToken);
            if (!outcome.Succeeded) return outcome.Failure!;

            if (string.IsNullOrWhiteSpace(outcome.Text))
                return Results.Problem("AI search returned empty response.");

            var payload = BookDiscoveryResponses.BookSearch(outcome.Text, aiResponseParser);
            if (payload is null)
                return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });

            if (!payload.IsBookQuery)
                return Results.BadRequest(new { error = payload.Message ?? "Query is not about books." });

            // An empty list from a query the model itself called a book query is
            // a refusal, not an answer — retry once on a different model. Not
            // when titles were extracted from a URL, where an empty list means
            // the page had none and a retry would only invite invention.
            if (payload.Books.Count == 0 && extractedTitles.Count == 0)
            {
                payload = await RetryBookSearchAsync(
                    payload, request.Query, extractedTitles, context, chat, aiResponseParser, cancellationToken);
            }

            return Results.Ok(new AiBookSearchResponse(payload.Summary, payload.Books));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for book-search: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ OpenAI book-search failed: {ExMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to run AI book search.");
        }
    }

    /// <summary>
    /// The second attempt. A failed retry is not an error — the first answer,
    /// empty book list and all, is still returned.
    /// </summary>
    private static async Task<BookSearchPayload> RetryBookSearchAsync(
        BookSearchPayload original,
        string query,
        IReadOnlyList<string> extractedTitles,
        HttpContext context,
        IAiChatCompletion chat,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var outcome = await chat.CompleteAsync(
            BookDiscoveryPrompts.BookSearchRetry(query, extractedTitles), context, cancellationToken);
        if (!outcome.Succeeded) return original;

        var retried = BookDiscoveryResponses.BookSearch(outcome.Text, aiResponseParser);
        if (retried is null) return original;

        return original with
        {
            Summary = retried.Summary ?? original.Summary,
            Books = retried.Books
        };
    }

    private static async Task<IResult> HandleMatchSeriesBooks(
        HttpContext context,
        [FromBody] MatchSeriesBooksRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat)
    {
        if (request is null || request.Books is null || request.Books.Count == 0)
            return Results.BadRequest(new { error = "Books list is required." });

        if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit) return overLimit;

        try
        {
            var outcome = await chat.CompleteAsync(
                BookDiscoveryPrompts.MatchSeriesBooks(modelSelection.GetModelFast(), request),
                context);
            if (!outcome.Succeeded) return outcome.Failure!;

            var matches = BookDiscoveryResponses.SeriesMatches(outcome.Text, aiResponseParser);
            Log.Information("Matched {MatchedCount} of {TotalCount} books",
                matches.Count(m => m.Status == "matched"), request.Books.Count);
            return Results.Ok(new MatchSeriesBooksResponse(matches));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for match-series-books: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Warning("OpenAI match-series-books failed: {ErrorMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to match series books.");
        }
    }

    private static async Task<IResult> HandleGroupSearchResults(
        HttpContext context,
        [FromBody] GroupSearchResultsRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat)
    {
        if (request is null || request.Books is null || request.Books.Count == 0)
            return Results.BadRequest(new { error = "Books list is required." });

        // Nothing to group — skip the OpenAI round-trip for the trivial case.
        if (request.Books.Count == 1)
            return Results.Ok(new GroupSearchResultsResponse([[request.Books[0].Md5]]));

        if (TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context) is { } overLimit) return overLimit;

        try
        {
            var outcome = await chat.CompleteAsync(
                BookDiscoveryPrompts.GroupSearchResults(modelSelection.GetModelFast(), request.Books),
                context);
            if (!outcome.Succeeded) return outcome.Failure!;

            var indexGroups = BookDiscoveryResponses.GroupIndices(outcome.Text, request.Books.Count, aiResponseParser);
            var md5Groups = indexGroups.Select(g => g.Select(i => request.Books[i].Md5).ToList()).ToList();

            return Results.Ok(new GroupSearchResultsResponse(md5Groups));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for group-search-results: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Warning("OpenAI group-search-results failed: {ErrorMessage}", ex.Message);
            return ApiResponse.InternalError("Failed to group search results.");
        }
    }

    private static bool HeaderIsTrue(HttpContext context, string header) =>
        context.Request.Headers.TryGetValue(header, out var value)
        && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Honours an <c>x-openai-model</c> override, but only onto a gpt-4 model —
    /// the header is a debugging affordance from the browser, and an arbitrary
    /// string here would be a way to spend the account's money on any model
    /// OpenAI happens to offer.
    /// </summary>
    private static string RequestedModel(HttpContext context, string fallback)
    {
        if (!context.Request.Headers.TryGetValue("x-openai-model", out var header)) return fallback;

        var requested = header.ToString();
        return !string.IsNullOrWhiteSpace(requested)
            && requested.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase)
                ? requested
                : fallback;
    }
}
