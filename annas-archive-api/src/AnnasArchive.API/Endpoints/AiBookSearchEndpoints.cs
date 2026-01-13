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
