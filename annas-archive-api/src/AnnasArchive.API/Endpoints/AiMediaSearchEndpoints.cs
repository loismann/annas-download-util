using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// AI-powered TV/movie discovery — same prompt-engineering JSON-in-JSON-out
/// pattern as AiBookSearchEndpoints' book-search, and now the same
/// <see cref="IAiChatCompletion"/> round trip. Unlike book search, this
/// deliberately does NOT resolve each suggested title against Sonarr/Radarr
/// server-side — that resolution is cheap (unlike Anna's Archive scraping) and
/// happens client-side instead, reusing the same searchTv()/searchMovies()
/// calls the normal search flow already makes.
/// </summary>
public static class AiMediaSearchEndpoints
{
    private const string SystemPrompt =
        @"You are a TV and movie discovery assistant. Determine whether the user query is asking for TV shows and/or movies.
If it is, return a list of relevant titles with a short reason each is a good match.
Return ONLY valid JSON with no markdown or extra text.";

    public static WebApplication MapAiMediaSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/ai/media-search", HandleMediaSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleMediaSearch(
        HttpContext context,
        [FromBody] AiMediaSearchRequest request,
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
            var outcome = await chat.CompleteAsync(
                SearchCall(modelSelection.GetModelDeep(), request.Query), context, cancellationToken);
            if (!outcome.Succeeded) return outcome.Failure!;

            if (string.IsNullOrWhiteSpace(outcome.Text))
                return Results.Problem("AI search returned empty response.");

            var root = Parse(outcome.Text, aiResponseParser);
            if (root is null)
                return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });

            using (root)
            {
                var isMediaQuery = root.RootElement.TryGetProperty("isMediaQuery", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                if (!isMediaQuery)
                {
                    return Results.BadRequest(new
                    {
                        error = OptionalString(root.RootElement, "message") ?? "Query is not about TV shows or movies."
                    });
                }

                var summary = OptionalString(root.RootElement, "summary");
                var results = ParseResults(root.RootElement);

                // An empty list from a query the model itself called a media
                // query is a refusal, not an answer — retry once on gpt-4o,
                // since asking the same model again identically is the one
                // thing guaranteed not to help.
                if (results.Count == 0)
                {
                    (summary, results) = await RetryAsync(
                        request.Query, summary, context, chat, aiResponseParser, cancellationToken);
                }

                return Results.Ok(new AiMediaSearchResponse(summary, results));
            }
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Information("❌ OpenAI media-search failed: {Message}", ex.Message);
            return ApiResponse.InternalError("Failed to run AI media search.");
        }
    }

    /// <summary>
    /// The second attempt. A failed retry keeps the first answer — an empty
    /// list is still a valid response shape.
    /// </summary>
    private static async Task<(string? Summary, List<AiMediaSearchItem> Results)> RetryAsync(
        string query,
        string? summary,
        HttpContext context,
        IAiChatCompletion chat,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var outcome = await chat.CompleteAsync(RetryCall(query), context, cancellationToken);
        if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.Text)) return (summary, []);

        var root = Parse(outcome.Text, aiResponseParser);
        if (root is null) return (summary, []);

        using (root)
        {
            return (OptionalString(root.RootElement, "summary") ?? summary, ParseResults(root.RootElement));
        }
    }

    private static AiChatCall SearchCall(string model, string query) => new(
        Endpoint: "media-search",
        Model: model,
        SystemPrompt: SystemPrompt,
        UserPrompt: $@"Query: ""{query}""

Return ONLY this JSON structure:
{{
  ""isMediaQuery"": boolean,
  ""message"": string|null,
  ""summary"": string|null,
  ""results"": [
    {{
      ""title"": ""Title"",
      ""year"": 1988,
      ""type"": ""tv|movie"",
      ""blurb"": ""1-2 sentence reason this matches the query""
    }}
  ]
}}

Rules:
- If the query is NOT about TV shows or movies, set isMediaQuery=false and return a brief message.
- ""type"" must be your best judgment of whether the title is normally catalogued as a TV series (""tv"") or a movie (""movie"") — a single query can mix both.
- If the query specifies a count (e.g. ""15 ...""), return that many results. Otherwise return 10-20.
- Make the summary 1-2 sentences explaining what the list represents and why (era, genre, acclaim, etc.).
- Keep each blurb concise (max 30 words).",
        MaxCompletionTokens: 2000,
        Temperature: 0.3,
        IsRetry: false);

    private static AiChatCall RetryCall(string query) => new(
        Endpoint: "media-search",
        Model: "gpt-4o",
        SystemPrompt: SystemPrompt,
        UserPrompt: $@"Query: ""{query}""

Return ONLY this JSON structure:
{{
  ""isMediaQuery"": true,
  ""message"": null,
  ""summary"": string|null,
  ""results"": [
    {{ ""title"": ""Title"", ""year"": 1988, ""type"": ""tv|movie"", ""blurb"": ""1-2 sentence reason"" }}
  ]
}}

Rules:
- You MUST return 10-20 results. Do not return an empty list.
- Keep each blurb concise (max 30 words).",
        MaxCompletionTokens: 2500,
        Temperature: 0.4,
        IsRetry: true);

    /// <summary>Null means the model's answer was not usable JSON, which the
    /// caller turns into "try again or simplify" rather than an empty list —
    /// the two mean different things to whoever is waiting.</summary>
    private static JsonDocument? Parse(string rawText, IAiResponseParser aiResponseParser)
    {
        var cleaned = aiResponseParser.StripCodeFences(rawText);

        try
        {
            var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;

            doc.Dispose();
            return null;
        }
        catch (JsonException ex)
        {
            Log.Information("❌ AI media-search JSON parse failed: {Message}", ex.Message);
            Log.Information("❌ AI media-search raw preview: {RawPreview}",
                rawText.Length > 2000 ? rawText[..2000] + "…" : rawText);
            return null;
        }
    }

    private static List<AiMediaSearchItem> ParseResults(JsonElement root)
    {
        var results = new List<AiMediaSearchItem>();
        if (!root.TryGetProperty("results", out var resultsProp) || resultsProp.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in resultsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var title = OptionalString(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var year = item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number
                ? y.GetInt32()
                : (int?)null;

            results.Add(new AiMediaSearchItem(
                title,
                year,
                OptionalString(item, "type") ?? "tv",
                OptionalString(item, "blurb")));
        }

        return results;
    }

    private static string? OptionalString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
