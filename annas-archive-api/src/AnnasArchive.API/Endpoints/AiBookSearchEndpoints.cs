using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Services;
using AnnasArchive.Core.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Book Search/Discovery endpoints.
/// </summary>
public static class AiBookSearchEndpoints
{
    // OpenLibrary author cache for the suggest-authors endpoint. Bounded, and
    // sized from the Caching:AuthorSuggestionCacheSize setting via
    // ConfigureCache below — that setting existed, was documented and had a
    // default, but nothing ever read it, so this cache previously grew without
    // limit for the life of the process.
    // Keys are book titles typed by a person, so they must match case-insensitively
    // — otherwise "Dune" and "dune" are two entries and one is a needless API call.
    private static LruCache<string, List<AuthorSuggestion>> _openLibraryAuthorCache =
        new(capacity: 500, ttl: HttpTimeouts.AuthorCacheTtl, keyComparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the configured capacity. Mirrors LibraryEpubCache.ConfigureCache
    /// and is called from ServiceConfiguration.ConfigureCaches at startup.
    /// </summary>
    public static void ConfigureCache(int capacity)
    {
        if (capacity > 0)
        {
            _openLibraryAuthorCache = new LruCache<string, List<AuthorSuggestion>>(capacity, HttpTimeouts.AuthorCacheTtl, StringComparer.OrdinalIgnoreCase);
            Log.Information("[AiBookSearch] Author suggestion cache configured with capacity {Capacity}", capacity);
        }
    }

    /// <summary>
    /// Maps AI Book Search endpoints to the application.
    /// </summary>
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
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BookTitle))
            return Results.BadRequest(new { error = "BookTitle is required." });

        try
        {
            var forceOpenAi = false;
            if (context.Request.Headers.TryGetValue("x-force-openai", out var forceHeader))
            {
                forceOpenAi = string.Equals(forceHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            }

            if (!forceOpenAi)
            {
                var openLibraryAuthors = await FetchAuthorsFromOpenLibraryAsync(request.BookTitle, httpFactory);
                if (openLibraryAuthors.Count > 0)
                {
                    Log.Information("✅ Author suggestions (OpenLibrary) for '{BookTitle}': {AuthorCount} authors found", request.BookTitle, openLibraryAuthors.Count);
                    return Results.Ok(new SuggestAuthorsResponse(openLibraryAuthors));
                }
            }

            var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
            if (tokenLimitResult is not null) return tokenLimitResult;

            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelFast();  // Uses gpt-4o by default
            if (context.Request.Headers.TryGetValue("x-openai-model", out var modelHeader))
            {
                var overrideModel = modelHeader.ToString();
                if (!string.IsNullOrWhiteSpace(overrideModel) &&
                    overrideModel.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase))
                {
                    model = overrideModel;
                }
            }

            var systemPrompt = @"You are a book metadata expert. Given a book title, suggest the 3-5 most likely authors sorted by probability. Return ONLY valid JSON with no markdown, explanation, or additional text.";

            var userPrompt = $@"Book title: ""{request.BookTitle}""

Return ONLY a JSON array of likely authors sorted by probability (most likely first). Each entry should have ""author"" (full name) and ""confidence"" (high/medium/low).

Example format:
[
  {{""author"": ""J.R.R. Tolkien"", ""confidence"": ""high""}},
  {{""author"": ""Christopher Tolkien"", ""confidence"": ""medium""}}
]

If the title is ambiguous or you don't recognize it, return an empty array: []

Do NOT include any markdown formatting, explanations, or text outside the JSON array.";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 500,
                temperature: 0.3
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "suggest-authors"), ("Model", model));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Error("❌ OpenAI suggest-authors failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Results.Problem(AiFailureMessage.ForResponse(response.StatusCode, body));
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rawText = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse the JSON array of authors
            var authors = new List<AuthorSuggestion>();
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                try
                {
                    // Remove markdown code blocks if present
                    var cleanedText = AiText.StripCodeFences(rawText);

                    // If the model adds extra text, extract the JSON array.
                    var arrayMatch = Regex.Match(cleanedText, @"\[[\s\S]*\]");
                    var jsonPayload = arrayMatch.Success ? arrayMatch.Value : cleanedText;

                    var authorsDoc = JsonDocument.Parse(jsonPayload);
                    if (authorsDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in authorsDoc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("author", out var authorProp) &&
                                item.TryGetProperty("confidence", out var confidenceProp))
                            {
                                authors.Add(new AuthorSuggestion(
                                    authorProp.GetString() ?? "",
                                    confidenceProp.GetString() ?? "low"
                                ));
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log.Information("⚠️ Failed to parse author suggestions JSON: {ExMessage}", ex.Message);
                    Log.Information("Raw text: {RawText}", rawText);
                    // Return empty array on parse failure
                }
            }

            Log.Information("✅ Author suggestions for '{BookTitle}': {AuthorCount} authors found", request.BookTitle, authors.Count);
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
        AnnasArchiveService annaArchiveService,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IWikipediaService wikipedia)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BookTitle) || string.IsNullOrWhiteSpace(request.Author))
            return Results.BadRequest(new { error = "BookTitle and Author are required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelFast();

            var systemPrompt = @"You are a literary expert with comprehensive knowledge of book series and author bibliographies. Given a book title and author, identify related books. Return ONLY valid JSON with no markdown or explanations.";

            var userPrompt = $@"Book: ""{request.BookTitle}"" by {request.Author}

Provide:
1. A summary of the current series (if this book is part of a series)
2. Other books in the SAME SERIES (if this book is part of a series)
3. OTHER SERIES by this author (different series they've written) with ALL books in each series

Return ONLY this JSON structure:
{{
  ""seriesSummary"": ""A 2-3 sentence overview of the current series, its themes, and significance. Null if not part of a series."",
  ""sameSeries"": [
    {{""title"": ""Book Title"", ""order"": 1, ""description"": ""Brief 1-line description""}}
  ],
  ""seriesName"": ""Series Name (optional)"",
  ""seriesSearchQuery"": ""Search query to find series books (optional)"",
  ""otherSeries"": [
    {{
      ""seriesName"": ""Series Name"",
      ""bookCount"": 3,
      ""books"": [
        {{""title"": ""Book 1 Title"", ""order"": 1, ""description"": ""Brief description""}}
      ],
      ""description"": ""Brief 1-line description of series"",
      ""summary"": ""2-3 sentence overview of this series""
    }}
  ]
}}

Rules:
- If the book is NOT part of a series, return null for seriesSummary
- If the series has MANY books, still return ALL known published titles (no ellipses)
- If you cannot list all titles, set seriesName and seriesSearchQuery for lookup
- For otherSeries, include ALL books in each series in the ""books"" array
- Only include PUBLISHED books (no unreleased/rumored books)
- Sort all books by publication/reading order
- For otherSeries, include 3-5 most notable series
- Each series summary should be 2-3 sentences covering themes, plot arc, and significance
- Keep individual book descriptions concise (max 15 words)
- Return ONLY the JSON object, no markdown formatting";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 3500,
                temperature: 0.3
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "related-books"), ("Model", model));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Error("❌ OpenAI related-books failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Results.Problem(AiFailureMessage.ForResponse(response.StatusCode, body));
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rawText = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse the JSON response
            var sameSeries = new List<SeriesBook>();
            var otherSeries = new List<AuthorSeries>();
            string? seriesName = null;
            string? seriesSearchQuery = null;
            string? seriesSummary = null;

            if (!string.IsNullOrWhiteSpace(rawText))
            {
                try
                {
                    // Remove markdown code blocks if present
                    var cleanedText = AiText.StripCodeFences(rawText);

                    var relatedDoc = JsonDocument.Parse(cleanedText);

                    // Parse seriesSummary
                    if (relatedDoc.RootElement.TryGetProperty("seriesSummary", out var summaryProp) &&
                        summaryProp.ValueKind == JsonValueKind.String)
                    {
                        seriesSummary = summaryProp.GetString();
                    }

                    // Parse sameSeries
                    if (relatedDoc.RootElement.TryGetProperty("sameSeries", out var sameSeriesArray) &&
                        sameSeriesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in sameSeriesArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("title", out var titleProp))
                            {
                                sameSeries.Add(new SeriesBook(
                                    titleProp.GetString() ?? "",
                                    item.TryGetProperty("order", out var orderProp) ? orderProp.GetInt32() : 0,
                                    item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                    null  // CoverUrl will be populated later
                                ));
                            }
                        }
                    }

                    if (relatedDoc.RootElement.TryGetProperty("seriesName", out var seriesNameProp) &&
                        seriesNameProp.ValueKind == JsonValueKind.String)
                    {
                        seriesName = seriesNameProp.GetString();
                    }

                    if (relatedDoc.RootElement.TryGetProperty("seriesSearchQuery", out var seriesSearchProp) &&
                        seriesSearchProp.ValueKind == JsonValueKind.String)
                    {
                        seriesSearchQuery = seriesSearchProp.GetString();
                    }

                    // Parse otherSeries
                    if (relatedDoc.RootElement.TryGetProperty("otherSeries", out var otherSeriesArray) &&
                        otherSeriesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in otherSeriesArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("seriesName", out var nameProp))
                            {
                                // Parse books array for this series
                                var seriesBooks = new List<SeriesBook>();
                                if (item.TryGetProperty("books", out var booksArray) &&
                                    booksArray.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var book in booksArray.EnumerateArray())
                                    {
                                        if (book.TryGetProperty("title", out var bookTitleProp))
                                        {
                                            seriesBooks.Add(new SeriesBook(
                                                bookTitleProp.GetString() ?? "",
                                                book.TryGetProperty("order", out var bookOrderProp) ? bookOrderProp.GetInt32() : 0,
                                                book.TryGetProperty("description", out var bookDescProp) ? bookDescProp.GetString() ?? "" : "",
                                                null  // CoverUrl will be populated later
                                            ));
                                        }
                                    }
                                }

                                otherSeries.Add(new AuthorSeries(
                                    nameProp.GetString() ?? "",
                                    item.TryGetProperty("bookCount", out var countProp) ? countProp.GetInt32() : seriesBooks.Count,
                                    seriesBooks,
                                    item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                    item.TryGetProperty("summary", out var seriesSummaryProp) ? seriesSummaryProp.GetString() ?? "" : ""
                                ));
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log.Information("⚠️ Failed to parse related books JSON: {ExMessage}", ex.Message);
                    Log.Information("Raw text: {RawText}", rawText);
                }
            }

            if (sameSeries.Count < 15)
            {
                string Normalize(string value) =>
                    Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

                var query = seriesSearchQuery ?? seriesName ?? $"{request.BookTitle} {request.Author}";
                try
                {
                    // 25, not 80 — Anna's Archive returns ~25 results per page, so
                    // 80 forced a second sequential page fetch (each fetch can be
                    // several seconds through Playwright) for marginal benefit;
                    // this is just confirming/expanding series titles by
                    // author+series substring match, not an exhaustive search.
                    var searchResults = await annaArchiveService.SearchAsync(query, 25, exact: false);
                    var normalizedAuthor = Normalize(request.Author);
                    var normalizedSeries = Normalize(seriesName ?? request.BookTitle);

                    var matches = searchResults
                        .Where(b => b.Authors.Any(a => Normalize(a).Contains(normalizedAuthor)))
                        .Select(b => b.Title)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct()
                        .Where(t => Normalize(t!).Contains(normalizedSeries))
                        .Select((t, index) => new SeriesBook(t!, index + 1, "", null))
                        .ToList();

                    if (matches.Count > sameSeries.Count)
                    {
                        sameSeries = matches;
                        Log.Information("✅ Series expanded via search: {MatchesCount} titles", matches.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Information("⚠️ Series expansion failed: {ExMessage}", ex.Message);
                }
            }

            // ───────── Fetch descriptions (Wikipedia -> GPT-4) ─────────
            // Google Books (quota exhausted) and OpenLibrary (down) were
            // removed from this chain — every call to either was a guaranteed
            // dead end that just added latency. Wikipedia is a real-data
            // source without those rate-limit problems; GPT regeneration is
            // now the last resort, not the routine outcome.
            // THROTTLED: Limit to MaxRelatedBookDescriptions to prevent rate limiting
            var maxDescriptions = AiThrottlingConfiguration.MaxRelatedBookDescriptions;
            Log.Information("[Books API] Fetching descriptions for up to {Max} books (sameSeries: {Count})...", maxDescriptions, sameSeries.Count);
            var descriptionLoopSw = Stopwatch.StartNew();

            // Process sameSeries books (limited)
            var sameSeriesProcessed = 0;
            for (int i = 0; i < sameSeries.Count && sameSeriesProcessed < maxDescriptions; i++)
            {
                var book = sameSeries[i];

                // Only fetch if description is missing or very short
                if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
                {
                    // Throttle between API calls
                    if (sameSeriesProcessed > 0)
                    {
                        await AiThrottlingConfiguration.ThrottleAsync();
                    }

                    var wikiDescription = await wikipedia.GetBookDescriptionAsync(book.Title, request.Author);

                    if (!string.IsNullOrWhiteSpace(wikiDescription))
                    {
                        sameSeries[i] = new SeriesBook(book.Title, book.Order, wikiDescription, book.CoverUrl, "wikipedia");
                        Log.Information("[Wikipedia] ✓ Got description for '{BookTitle}'", book.Title);
                    }
                    else
                    {
                        // Fallback to GPT-4 generated no-spoiler description
                        var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                            book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                        sameSeries[i] = new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt");
                        Log.Information("[GPT-4] ✓ Generated description for '{BookTitle}'", book.Title);
                    }

                    sameSeriesProcessed++;
                }
            }

            if (sameSeries.Count > maxDescriptions)
            {
                Log.Information("[Books API] Skipped {Count} sameSeries books (over limit)", sameSeries.Count - maxDescriptions);
            }

            // Process otherSeries books (limited - share quota with sameSeries)
            var remainingQuota = Math.Max(0, maxDescriptions - sameSeriesProcessed);
            var otherSeriesProcessed = 0;
            Log.Information("[Books API] Remaining description quota for otherSeries: {Quota}", remainingQuota);

            for (int i = 0; i < otherSeries.Count && otherSeriesProcessed < remainingQuota; i++)
            {
                var series = otherSeries[i];
                var updatedBooks = new List<SeriesBook>();

                foreach (var book in series.Books)
                {
                    if (otherSeriesProcessed >= remainingQuota)
                    {
                        // Over quota - keep original book without fetching description
                        updatedBooks.Add(book);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
                    {
                        // Throttle between API calls
                        if (otherSeriesProcessed > 0 || sameSeriesProcessed > 0)
                        {
                            await AiThrottlingConfiguration.ThrottleAsync();
                        }

                        var wikiDescription = await wikipedia.GetBookDescriptionAsync(book.Title, request.Author);

                        if (!string.IsNullOrWhiteSpace(wikiDescription))
                        {
                            updatedBooks.Add(new SeriesBook(book.Title, book.Order, wikiDescription, book.CoverUrl, "wikipedia"));
                            Log.Information("[Wikipedia] ✓ Got description for '{BookTitle}'", book.Title);
                        }
                        else
                        {
                            var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                            updatedBooks.Add(new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt"));
                            Log.Information("[GPT-4] ✓ Generated description for '{BookTitle}'", book.Title);
                        }

                        otherSeriesProcessed++;
                    }
                    else
                    {
                        updatedBooks.Add(book);
                    }
                }

                otherSeries[i] = new AuthorSeries(
                    series.SeriesName,
                    series.BookCount,
                    updatedBooks,
                    series.Description,
                    series.Summary
                );
            }

            var totalDescriptions = sameSeriesProcessed + otherSeriesProcessed;
            Log.Information("[Books API] Fetched {Total} descriptions (sameSeries: {Same}, otherSeries: {Other})",
                totalDescriptions, sameSeriesProcessed, otherSeriesProcessed);
            PerfLog.Record("RelatedBooks.DescriptionLoop", descriptionLoopSw.Elapsed.TotalMilliseconds, true,
                ("TotalDescriptions", totalDescriptions), ("SameSeries", sameSeriesProcessed), ("OtherSeries", otherSeriesProcessed));

            Log.Information("✅ Related books for '{RequestBookTitle}': {SameSeriesCount} series books, {OtherSeriesCount} other series", request.BookTitle, sameSeries.Count, otherSeries.Count);

            return Results.Ok(new RelatedBooksResponse(sameSeries, otherSeries, seriesSummary));
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
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "query is required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelDeep();
            var hasUrl = request.Query.Contains("http://", StringComparison.OrdinalIgnoreCase)
                || request.Query.Contains("https://", StringComparison.OrdinalIgnoreCase);
            var extractedTitles = hasUrl
                ? await BookTitleExtractionHelpers.ExtractBookTitlesFromQueryAsync(request.Query, httpFactory, cancellationToken)
                : new List<string>();
            var hasExtractedTitles = extractedTitles.Count > 0;
            var maxResults = hasExtractedTitles
                ? Math.Min(20, extractedTitles.Count)
                : 20;
            var perBookWordLimit = hasExtractedTitles && extractedTitles.Count >= 60 ? 24 : 45;

            var systemPrompt = @"You are a book discovery assistant. Determine whether the user query is asking for books.
If it is, return a list of relevant books with an engaging, spoiler-free summary of the search.
Return ONLY valid JSON with no markdown or extra text.";

            var extractedBlock = hasExtractedTitles
                ? $"ExtractedTitles (from the URL):\n- {string.Join("\n- ", extractedTitles.Take(100))}\n"
                : "ExtractedTitles: None\n";

            var userPrompt = $@"Query: ""{request.Query}""
{extractedBlock}

Return ONLY this JSON structure:
{{
  ""isBookQuery"": boolean,
  ""message"": string|null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- If the query is NOT about books, set isBookQuery=false and return a brief message.
- If ExtractedTitles are provided, return those titles in that order and fill in author if known; do not invent titles not present.
- If ExtractedTitles are not provided, return up to {maxResults} books when the query includes a URL or asks for a list; otherwise return 10-25.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- The summary should briefly explain what the list represents and why it's notable (e.g., award significance, era, genre influence).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: hasUrl ? 6000 : 2000,
                temperature: 0.3
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "book-search"), ("Model", model), ("Retry", false));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Error("❌ OpenAI book-search failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Results.Problem(AiFailureMessage.ForResponse(response.StatusCode, body));
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rawText = aiResponseParser.ExtractText(doc.RootElement);

            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            if (string.IsNullOrWhiteSpace(rawText))
                return Results.Problem("AI search returned empty response.");

            var cleaned = AiText.StripCodeFences(rawText);

            JsonDocument resultDoc;
            try
            {
                resultDoc = JsonDocument.Parse(cleaned);
            }
            catch (Exception ex)
            {
                var rawPreview = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
                var cleanPreview = cleaned.Length > 2000 ? cleaned[..2000] + "…" : cleaned;
                Log.Information("❌ AI book-search JSON parse failed: {ExMessage}", ex.Message);
                Log.Information("❌ AI book-search raw preview: {RawPreview}", rawPreview);
                Log.Information("❌ AI book-search cleaned preview: {CleanPreview}", cleanPreview);
                return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });
            }

            var root = resultDoc.RootElement;

            var isBookQuery = root.TryGetProperty("isBookQuery", out var bookProp) && bookProp.ValueKind == JsonValueKind.True;
            if (!isBookQuery)
            {
                var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Query is not about books.";
                return Results.BadRequest(new { error = message ?? "Query is not about books." });
            }

            var summary = root.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null;
            var books = new List<AiBookSearchItem>();

            if (root.TryGetProperty("books", out var booksProp) && booksProp.ValueKind == JsonValueKind.Array)
            {
                // Deliberately NOT calling googleBooks/openLibrary/coverLookupService here
                // per-book — the single OpenAI call above already generated a usable
                // summary for every book, and re-fetching descriptions from Google
                // Books (quota-exhausted) and OpenLibrary (currently down) one book
                // at a time was adding several seconds of dead-end HTTP calls PER
                // BOOK to the response. Covers are fetched lazily by the frontend
                // after this list renders (same pattern as search results), instead
                // of blocking the whole response on them here.
                foreach (var item in booksProp.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                    var gptSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(title)) continue;

                    books.Add(new AiBookSearchItem(title, author, gptSummary, importance, null, "gpt"));
                }
            }

            if (books.Count == 0 && !hasExtractedTitles)
            {
                var retryPrompt = $@"Query: ""{request.Query}""

Return ONLY this JSON structure:
{{
  ""isBookQuery"": true,
  ""message"": null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- You MUST return 10-20 books. Do not return an empty list.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

                var retryPayload = modelHelper.BuildChatCompletionPayload(
                    "gpt-4o",
                    new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = retryPrompt }
                    },
                    maxCompletionTokens: 2500,
                    temperature: 0.4
                );

                var retrySw = Stopwatch.StartNew();
                var retryResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", retryPayload, cancellationToken);
                PerfLog.Record("OpenAI.ChatCompletion", retrySw.Elapsed.TotalMilliseconds, retryResponse.IsSuccessStatusCode, ("Endpoint", "book-search"), ("Model", "gpt-4o"), ("Retry", true));
                if (retryResponse.IsSuccessStatusCode)
                {
                    using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var retryDoc = await JsonDocument.ParseAsync(retryStream, cancellationToken: cancellationToken);
                    var retryText = aiResponseParser.ExtractText(retryDoc.RootElement);
                    if (!string.IsNullOrWhiteSpace(retryText))
                    {
                        var retryClean = AiText.StripCodeFences(retryText);

                        var retryResultDoc = JsonDocument.Parse(retryClean);
                        var retryRoot = retryResultDoc.RootElement;
                        summary = retryRoot.TryGetProperty("summary", out var retrySummary) ? retrySummary.GetString() : summary;

                        if (retryRoot.TryGetProperty("books", out var retryBooks) && retryBooks.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in retryBooks.EnumerateArray())
                            {
                                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                                var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                                var gptSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                                var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                                if (string.IsNullOrWhiteSpace(title)) continue;

                                // Same rationale as the main path above — use the
                                // AI's own summary directly, cover fetched lazily
                                // by the frontend afterward.
                                books.Add(new AiBookSearchItem(title, author, gptSummary, importance, null, "gpt"));
                            }
                        }
                    }
                }
            }

            return Results.Ok(new AiBookSearchResponse(summary, books));
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

    private static async Task<IResult> HandleMatchSeriesBooks(
        HttpContext context,
        [FromBody] MatchSeriesBooksRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (request is null || request.Books is null || request.Books.Count == 0)
            return Results.BadRequest(new { error = "Books list is required." });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelFast();

            // Build a comprehensive prompt with all search results
            var booksJson = JsonSerializer.Serialize(request.Books, new JsonSerializerOptions { WriteIndented = true });

            var systemPrompt = @"You are an expert book matcher. You analyze search results from a library database and select the best match for each book in a series.

Your task: For each book, examine all search result candidates and select the BEST match based on:
1. Title match (handle variations like subtitles, series numbers in parentheses)
2. Author match (exact or close match)
3. Format match (if specified)
4. Detect and AVOID: Omnibus editions, anthologies, collections, combined volumes
5. Prefer standalone individual books over compilations

Return ONLY valid JSON with no markdown or explanation.";

            var userPrompt = $@"Series: ""{request.SeriesName ?? "Unknown Series"}""
Author: ""{request.Author}""
Preferred Format: ""{request.PreferredFormat ?? "ANY"}""

For each book below, I'm providing the title we're looking for and the search results. Select the BEST candidate or flag if no good match exists.

Books and Search Results:
{booksJson}

Return ONLY this JSON structure:
{{
  ""matches"": [
    {{
      ""bookTitle"": ""Book title we searched for"",
      ""order"": 1,
      ""status"": ""matched|ambiguous|not_found"",
      ""selectedMd5"": ""md5_of_best_match"",
      ""selectedTitle"": ""Full title from search results"",
      ""confidence"": ""exact|likely|uncertain"",
      ""reason"": ""Brief explanation (e.g., 'Exact title and author match', 'Anthology detected', etc.)""
    }}
  ]
}}

Rules:
- status: ""matched"" if you found a good match, ""ambiguous"" if multiple viable options, ""not_found"" if no good match
- confidence: ""exact"" for perfect matches, ""likely"" for close matches, ""uncertain"" if you're not sure
- ALWAYS avoid omnibus/anthology editions unless that's the ONLY option
- If a book has ""(Books 1-3)"" or ""Complete Series"" in the title, flag it as ambiguous or not_found
- Match format if specified (e.g., only select EPUB if format is EPUB)";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 2000,
                temperature: 0.2
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "match-series-books"), ("Model", model));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Error("❌ OpenAI match-series-books failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return Results.Problem(AiFailureMessage.ForResponse(response.StatusCode, body));
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rawText = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse the JSON response
            var matches = new List<SeriesBookMatch>();

            if (!string.IsNullOrWhiteSpace(rawText))
            {
                try
                {
                    // Remove markdown code blocks if present
                    var cleanedText = AiText.StripCodeFences(rawText);

                    var matchDoc = JsonDocument.Parse(cleanedText);

                    if (matchDoc.RootElement.TryGetProperty("matches", out var matchesArray) &&
                        matchesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in matchesArray.EnumerateArray())
                        {
                            matches.Add(new SeriesBookMatch(
                                item.TryGetProperty("bookTitle", out var bt) ? bt.GetString() ?? "" : "",
                                item.TryGetProperty("order", out var ord) ? ord.GetInt32() : 0,
                                item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                                item.TryGetProperty("selectedMd5", out var md5) ? md5.GetString() : null,
                                item.TryGetProperty("selectedTitle", out var title) ? title.GetString() : null,
                                item.TryGetProperty("confidence", out var conf) ? conf.GetString() ?? "" : "",
                                item.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : ""
                            ));
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log.Information("⚠️ Failed to parse series match JSON: {ExMessage}", ex.Message);
                    Log.Information("Raw text: {RawText}", rawText);
                }
            }

            Log.Information("Matched {MatchedCount} of {TotalCount} books", matches.Count(m => m.Status == "matched"), request.Books.Count);
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
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        if (request is null || request.Books is null || request.Books.Count == 0)
            return Results.BadRequest(new { error = "Books list is required." });

        // Nothing to group — skip the OpenAI round-trip for the trivial case.
        if (request.Books.Count == 1)
            return Results.Ok(new GroupSearchResultsResponse(new List<List<string>> { new() { request.Books[0].Md5 } }));

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelFast();

            // Index-only payload — asking the model to faithfully echo back
            // 32-char md5 hashes for 50-100+ books risks silent transcription
            // errors that would misfile a book into the wrong group or drop
            // it from the response entirely. Small integer indices round-trip
            // reliably; we map back to md5 ourselves afterward using the same
            // array we sent (see ParseGroupIndices).
            var indexedBooks = request.Books
                .Select((b, i) => new { index = i, title = b.Title, authors = b.Authors, format = b.Format, year = b.Year })
                .ToList();
            var booksJson = JsonSerializer.Serialize(indexedBooks, new JsonSerializerOptions { WriteIndented = true });

            var systemPrompt = @"You are a library cataloging assistant. You'll receive a JSON array of book search results, each with an index, title, authors, format, and year. Many entries represent the SAME underlying book — a different file format (EPUB/PDF/MOBI/AZW3/etc.) or a duplicate upload/scan of the identical edition.

Your task: group indices that represent the same book together. Format never matters for grouping — EPUB and PDF copies of the same book belong in the same group. Do NOT group:
- Different volumes/books in a series (e.g. a book titled ""Book 2"" or ""#2"" is a DIFFERENT book from ""Book 1"" or the base title with no number)
- Different, unrelated books that merely share a similar title
- Meaningfully different editions (e.g. abridged vs unabridged, a translation vs the original) unless you're confident they're the same core work

Every index from the input must appear in exactly one group in the output. A book with no duplicates is still its own group of one.

Return ONLY valid JSON with no markdown or explanation.";

            var userPrompt = $@"Books:
{booksJson}

Return ONLY this JSON structure:
{{
  ""groups"": [[0, 3, 7], [1], [2, 5]]
}}

Each inner array is a list of indices that are the same book.";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 4000,
                temperature: 0.1
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "group-search-results"), ("Model", model));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI group-search-results failed status={Status} body={Body}", (int)response.StatusCode, body);
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var rawText = aiResponseParser.ExtractText(doc.RootElement);

            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            var indexGroups = ParseGroupIndices(rawText, request.Books.Count);
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

    /// <summary>Parses the model's {"groups":[[...]]} response into validated
    /// index groups, defensively covering every index 0..count-1 exactly
    /// once — any index the model omitted becomes its own singleton group,
    /// and any index it duplicated across groups is dropped on the later
    /// occurrence, so a parsing hiccup degrades to "no grouping" for the
    /// affected books rather than silently dropping them from the results.</summary>
    private static List<List<int>> ParseGroupIndices(string? rawText, int count)
    {
        var groups = new List<List<int>>();
        var seen = new HashSet<int>();

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                var cleanedText = AiText.StripCodeFences(rawText);

                var groupDoc = JsonDocument.Parse(cleanedText);
                if (groupDoc.RootElement.TryGetProperty("groups", out var groupsArray) && groupsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var groupEl in groupsArray.EnumerateArray())
                    {
                        if (groupEl.ValueKind != JsonValueKind.Array) continue;
                        var indices = new List<int>();
                        foreach (var idxEl in groupEl.EnumerateArray())
                        {
                            if (idxEl.ValueKind != JsonValueKind.Number) continue;
                            var idx = idxEl.GetInt32();
                            if (idx < 0 || idx >= count) continue; // out-of-range, ignore
                            if (!seen.Add(idx)) continue; // already claimed by an earlier group
                            indices.Add(idx);
                        }
                        if (indices.Count > 0) groups.Add(indices);
                    }
                }
            }
            catch (JsonException ex)
            {
                Log.Information("⚠️ Failed to parse group-search-results JSON: {Message}", ex.Message);
                Log.Information("Raw text: {RawText}", rawText);
            }
        }

        // Any index the model never mentioned (parse failure, omission, etc.)
        // still needs to show up — as its own singleton group.
        for (var i = 0; i < count; i++)
        {
            if (seen.Add(i)) groups.Add(new List<int> { i });
        }

        return groups;
    }

    #region OpenLibrary Author Cache Helpers

    // The cache compares keys case-insensitively itself, so these only need to
    // trim. TTL and eviction now live in LruCache rather than being re-checked
    // by hand at each call site.
    private static bool TryGetOpenLibraryAuthorCache(string title, out List<AuthorSuggestion> authors)
    {
        if (_openLibraryAuthorCache.TryGetValue(title.Trim(), out var cached))
        {
            authors = cached;
            return true;
        }

        authors = new List<AuthorSuggestion>();
        return false;
    }

    private static void SetOpenLibraryAuthorCache(string title, List<AuthorSuggestion> authors) =>
        _openLibraryAuthorCache.Set(title.Trim(), authors);

    private static async Task<List<AuthorSuggestion>> FetchAuthorsFromOpenLibraryAsync(string title, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(title)) return new List<AuthorSuggestion>();

        if (TryGetOpenLibraryAuthorCache(title, out var cached))
            return cached;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = HttpTimeouts.OpenLibraryCacheLookup;

            var query = Uri.EscapeDataString(title.Trim());
            var url = $"https://openlibrary.org/search.json?title={query}&limit=10";
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<AuthorSuggestion>();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                return new List<AuthorSuggestion>();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in docs.EnumerateArray())
            {
                if (!item.TryGetProperty("author_name", out var authorNames) || authorNames.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var author in authorNames.EnumerateArray())
                {
                    var name = author.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var authorKey = name.Trim();
                    counts[authorKey] = counts.TryGetValue(authorKey, out var existing) ? existing + 1 : 1;
                }
            }

            if (counts.Count == 0) return new List<AuthorSuggestion>();

            var max = counts.Values.Max();
            string ConfidenceFromScore(int score)
            {
                var ratio = score / (double)max;
                if (ratio >= 0.66) return "high";
                if (ratio >= 0.34) return "medium";
                return "low";
            }

            var results = counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(5)
                .Select(kv => new AuthorSuggestion(kv.Key, ConfidenceFromScore(kv.Value)))
                .ToList();
            SetOpenLibraryAuthorCache(title, results);
            return results;
        }
        catch
        {
            return new List<AuthorSuggestion>();
        }
    }

    #endregion
}
