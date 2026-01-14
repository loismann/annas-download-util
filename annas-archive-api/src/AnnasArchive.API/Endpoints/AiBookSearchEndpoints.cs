using System.Text.Json;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Book Search/Discovery endpoints.
/// </summary>
public static class AiBookSearchEndpoints
{
    // OpenLibrary author cache for suggest-authors endpoint
    private static readonly TimeSpan OpenLibraryAuthorCacheTtl = TimeSpan.FromHours(6);
    private static readonly Dictionary<string, (DateTime fetchedAt, List<AuthorSuggestion> authors)> OpenLibraryAuthorCache = new();
    private static readonly object OpenLibraryAuthorCacheLock = new();

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
                    Console.WriteLine($"✅ Author suggestions (OpenLibrary) for '{request.BookTitle}': {openLibraryAuthors.Count} authors found");
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

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI suggest-authors failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
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
                    var cleanedText = rawText.Trim();
                    if (cleanedText.StartsWith("```"))
                    {
                        cleanedText = cleanedText
                            .Replace("```json", "")
                            .Replace("```", "")
                            .Trim();
                    }

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
                    Console.WriteLine($"⚠️ Failed to parse author suggestions JSON: {ex.Message}");
                    Console.WriteLine($"Raw text: {rawText}");
                    // Return empty array on parse failure
                }
            }

            Console.WriteLine($"✅ Author suggestions for '{request.BookTitle}': {authors.Count} authors found");
            return Results.Ok(new SuggestAuthorsResponse(authors));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OpenAI suggest-authors failed: {ex.Message}");
            return ApiResponse.InternalError("Failed to suggest authors.");
        }
    }

    private static async Task<IResult> HandleRelatedBooks(
        HttpContext context,
        [FromBody] RelatedBooksRequest request,
        AnnaArchiveService annaArchiveService,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IGoogleBooksService googleBooks,
        IOpenLibraryService openLibrary)
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

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI related-books failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
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
                    var cleanedText = rawText.Trim();
                    if (cleanedText.StartsWith("```"))
                    {
                        cleanedText = cleanedText
                            .Replace("```json", "")
                            .Replace("```", "")
                            .Trim();
                    }

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
                    Console.WriteLine($"⚠️ Failed to parse related books JSON: {ex.Message}");
                    Console.WriteLine($"Raw text: {rawText}");
                }
            }

            if (sameSeries.Count < 15)
            {
                string Normalize(string value) =>
                    Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

                var query = seriesSearchQuery ?? seriesName ?? $"{request.BookTitle} {request.Author}";
                try
                {
                    var searchResults = await annaArchiveService.SearchAsync(query, 80, exact: false);
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
                        Console.WriteLine($"✅ Series expanded via search: {matches.Count} titles");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Series expansion failed: {ex.Message}");
                }
            }

            // ───────── Fetch descriptions (Google Books -> OpenLibrary -> GPT-4) ─────────
            Console.WriteLine("[Books API] Fetching descriptions for books...");

            // Process sameSeries books
            for (int i = 0; i < sameSeries.Count; i++)
            {
                var book = sameSeries[i];

                // Only fetch if description is missing or very short
                if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
                {
                    // Try Google Books first
                    var gbDescription = await googleBooks.GetBookDescriptionAsync(book.Title, request.Author);

                    if (!string.IsNullOrWhiteSpace(gbDescription))
                    {
                        sameSeries[i] = new SeriesBook(book.Title, book.Order, gbDescription, book.CoverUrl, "googlebooks");
                        Console.WriteLine($"[GoogleBooks] ✓ Got description for '{book.Title}'");
                    }
                    else
                    {
                        // Fallback to OpenLibrary
                        var olDescription = await openLibrary.GetBookDescriptionAsync(book.Title, request.Author);

                        if (!string.IsNullOrWhiteSpace(olDescription))
                        {
                            sameSeries[i] = new SeriesBook(book.Title, book.Order, olDescription, book.CoverUrl, "openlibrary");
                            Console.WriteLine($"[OpenLibrary] ✓ Got description for '{book.Title}'");
                        }
                        else
                        {
                            // Fallback to GPT-4 generated no-spoiler description
                            var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                            sameSeries[i] = new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt");
                            Console.WriteLine($"[GPT-4] ✓ Generated description for '{book.Title}'");
                        }
                    }
                }
            }

            // Process otherSeries books
            for (int i = 0; i < otherSeries.Count; i++)
            {
                var series = otherSeries[i];
                var updatedBooks = new List<SeriesBook>();

                foreach (var book in series.Books)
                {
                    if (string.IsNullOrWhiteSpace(book.Description) || book.Description.Length < 10)
                    {
                        // Try Google Books first
                        var gbDescription = await googleBooks.GetBookDescriptionAsync(book.Title, request.Author);

                        if (!string.IsNullOrWhiteSpace(gbDescription))
                        {
                            updatedBooks.Add(new SeriesBook(book.Title, book.Order, gbDescription, book.CoverUrl, "googlebooks"));
                            Console.WriteLine($"[GoogleBooks] ✓ Got description for '{book.Title}'");
                        }
                        else
                        {
                            // Fallback to OpenLibrary
                            var olDescription = await openLibrary.GetBookDescriptionAsync(book.Title, request.Author);

                            if (!string.IsNullOrWhiteSpace(olDescription))
                            {
                                updatedBooks.Add(new SeriesBook(book.Title, book.Order, olDescription, book.CoverUrl, "openlibrary"));
                                Console.WriteLine($"[OpenLibrary] ✓ Got description for '{book.Title}'");
                            }
                            else
                            {
                                var gptDescription = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                    book.Title, request.Author, http, model, modelHelper, aiResponseParser);
                                updatedBooks.Add(new SeriesBook(book.Title, book.Order, gptDescription, book.CoverUrl, "gpt"));
                                Console.WriteLine($"[GPT-4] ✓ Generated description for '{book.Title}'");
                            }
                        }
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

            Console.WriteLine($"✅ Related books for '{request.BookTitle}': {sameSeries.Count} series books, {otherSeries.Count} other series");

            return Results.Ok(new RelatedBooksResponse(sameSeries, otherSeries, seriesSummary));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OpenAI related-books failed: {ex.Message}");
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
        IGoogleBooksService googleBooks,
        IOpenLibraryService openLibrary,
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

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI book-search failed status={(int)response.StatusCode} body={body}");
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

            if (string.IsNullOrWhiteSpace(rawText))
                return Results.Problem("AI search returned empty response.");

            var cleaned = rawText.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            JsonDocument resultDoc;
            try
            {
                resultDoc = JsonDocument.Parse(cleaned);
            }
            catch (Exception ex)
            {
                var rawPreview = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
                var cleanPreview = cleaned.Length > 2000 ? cleaned[..2000] + "…" : cleaned;
                Console.WriteLine($"❌ AI book-search JSON parse failed: {ex.Message}");
                Console.WriteLine($"❌ AI book-search raw preview: {rawPreview}");
                Console.WriteLine($"❌ AI book-search cleaned preview: {cleanPreview}");
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
                foreach (var item in booksProp.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                    var gptSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // Try Google Books -> OpenLibrary -> GPT-4
                    string bookSummary = gptSummary;
                    string? descriptionSource = null;

                    // Try Google Books first
                    var gbDescription = await googleBooks.GetBookDescriptionAsync(title, author);

                    if (!string.IsNullOrWhiteSpace(gbDescription))
                    {
                        bookSummary = gbDescription;
                        descriptionSource = "googlebooks";
                        Console.WriteLine($"[GoogleBooks] ✓ Got description for '{title}' by {author}");
                    }
                    else
                    {
                        // Fallback to OpenLibrary
                        var olDescription = await openLibrary.GetBookDescriptionAsync(title, author);

                        if (!string.IsNullOrWhiteSpace(olDescription))
                        {
                            bookSummary = olDescription;
                            descriptionSource = "openlibrary";
                            Console.WriteLine($"[OpenLibrary] ✓ Got description for '{title}' by {author}");
                        }
                        else if (string.IsNullOrWhiteSpace(gptSummary))
                        {
                            // If all sources failed, generate a fallback
                            bookSummary = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                title, author, http, model, modelHelper, aiResponseParser);
                            descriptionSource = "gpt";
                            Console.WriteLine($"[GPT-4] ✓ Generated fallback description for '{title}'");
                        }
                        else
                        {
                            // Use GPT summary as fallback
                            descriptionSource = "gpt";
                            Console.WriteLine($"[GPT-4] ✓ Using GPT-generated description for '{title}' (no external sources)");
                        }
                    }

                    var coverUrl = await openLibrary.GetCoverUrlAsync(title, author)
                                   ?? await googleBooks.GetCoverUrlAsync(title, author);

                    books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl, descriptionSource));
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

                var retryResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", retryPayload, cancellationToken);
                if (retryResponse.IsSuccessStatusCode)
                {
                    using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var retryDoc = await JsonDocument.ParseAsync(retryStream, cancellationToken: cancellationToken);
                    var retryText = aiResponseParser.ExtractText(retryDoc.RootElement);
                    if (!string.IsNullOrWhiteSpace(retryText))
                    {
                        var retryClean = retryText.Trim();
                        if (retryClean.StartsWith("```"))
                        {
                            retryClean = retryClean
                                .Replace("```json", "")
                                .Replace("```", "")
                                .Trim();
                        }

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

                                // Try Google Books -> OpenLibrary -> GPT-4 (retry path)
                                string bookSummary = gptSummary;
                                string? descriptionSource = null;

                                // Try Google Books first
                                var gbDescription = await googleBooks.GetBookDescriptionAsync(title, author);

                                if (!string.IsNullOrWhiteSpace(gbDescription))
                                {
                                    bookSummary = gbDescription;
                                    descriptionSource = "googlebooks";
                                    Console.WriteLine($"[GoogleBooks] ✓ Got description for '{title}' by {author} (retry)");
                                }
                                else
                                {
                                    // Fallback to OpenLibrary
                                    var olDescription = await openLibrary.GetBookDescriptionAsync(title, author);

                                    if (!string.IsNullOrWhiteSpace(olDescription))
                                    {
                                        bookSummary = olDescription;
                                        descriptionSource = "openlibrary";
                                        Console.WriteLine($"[OpenLibrary] ✓ Got description for '{title}' by {author} (retry)");
                                    }
                                    else if (string.IsNullOrWhiteSpace(gptSummary))
                                    {
                                        // If all sources failed, generate a fallback
                                        bookSummary = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                                            title, author, http, model, modelHelper, aiResponseParser);
                                        descriptionSource = "gpt";
                                        Console.WriteLine($"[GPT-4] ✓ Generated fallback description for '{title}' (retry)");
                                    }
                                    else
                                    {
                                        // Use GPT summary as fallback
                                        descriptionSource = "gpt";
                                        Console.WriteLine($"[GPT-4] ✓ Using GPT-generated description for '{title}' (retry, no external sources)");
                                    }
                                }

                                var coverUrl = await openLibrary.GetCoverUrlAsync(title, author)
                                               ?? await googleBooks.GetCoverUrlAsync(title, author);

                                books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl, descriptionSource));
                            }
                        }
                    }
                }
            }

            return Results.Ok(new AiBookSearchResponse(summary, books));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OpenAI book-search failed: {ex.Message}");
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

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI match-series-books failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
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
                    var cleanedText = rawText.Trim();
                    if (cleanedText.StartsWith("```"))
                    {
                        cleanedText = cleanedText
                            .Replace("```json", "")
                            .Replace("```", "")
                            .Trim();
                    }

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
                    Console.WriteLine($"⚠️ Failed to parse series match JSON: {ex.Message}");
                    Console.WriteLine($"Raw text: {rawText}");
                }
            }

            Console.WriteLine($"✅ Matched {matches.Count(m => m.Status == "matched")} of {request.Books.Count} books");
            return Results.Ok(new MatchSeriesBooksResponse(matches));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OpenAI match-series-books failed: {ex.Message}");
            return ApiResponse.InternalError("Failed to match series books.");
        }
    }

    #region OpenLibrary Author Cache Helpers

    private static bool TryGetOpenLibraryAuthorCache(string title, out List<AuthorSuggestion> authors)
    {
        var key = title.Trim().ToLowerInvariant();
        lock (OpenLibraryAuthorCacheLock)
        {
            if (OpenLibraryAuthorCache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow - entry.fetchedAt <= OpenLibraryAuthorCacheTtl)
                {
                    authors = entry.authors;
                    return true;
                }
                OpenLibraryAuthorCache.Remove(key);
            }
        }

        authors = new List<AuthorSuggestion>();
        return false;
    }

    private static void SetOpenLibraryAuthorCache(string title, List<AuthorSuggestion> authors)
    {
        var key = title.Trim().ToLowerInvariant();
        lock (OpenLibraryAuthorCacheLock)
        {
            OpenLibraryAuthorCache[key] = (DateTime.UtcNow, authors);
        }
    }

    private static async Task<List<AuthorSuggestion>> FetchAuthorsFromOpenLibraryAsync(string title, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(title)) return new List<AuthorSuggestion>();

        if (TryGetOpenLibraryAuthorCache(title, out var cached))
            return cached;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);

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
