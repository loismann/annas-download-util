using System.Diagnostics;
using System.Text.Json;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Services;
using AnnasArchive.Core.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// AI-powered TV/movie discovery — same prompt-engineering JSON-in-JSON-out
/// pattern as AiBookSearchEndpoints' book-search (reuses the same "OpenAI"
/// HttpClient, model selection, and response parser). Unlike book search,
/// this deliberately does NOT resolve each suggested title against
/// Sonarr/Radarr server-side — that resolution is cheap (unlike Anna's
/// Archive scraping) and happens client-side instead, reusing the same
/// searchTv()/searchMovies() calls the normal search flow already makes.
/// </summary>
public static class AiMediaSearchEndpoints
{
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

            const string systemPrompt = @"You are a TV and movie discovery assistant. Determine whether the user query is asking for TV shows and/or movies.
If it is, return a list of relevant titles with a short reason each is a good match.
Return ONLY valid JSON with no markdown or extra text.";

            var userPrompt = $@"Query: ""{request.Query}""

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
- Keep each blurb concise (max 30 words).";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 2000,
                temperature: 0.3
            );

            var aiSw = Stopwatch.StartNew();
            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, cancellationToken);
            PerfLog.Record("OpenAI.ChatCompletion", aiSw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, ("Endpoint", "media-search"), ("Model", model), ("Retry", false));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Information("❌ OpenAI media-search failed status={StatusCode} body={Body}", (int)response.StatusCode, body);
                return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
                cleaned = cleaned.Replace("```json", "").Replace("```", "").Trim();
            }

            JsonDocument resultDoc;
            try
            {
                resultDoc = JsonDocument.Parse(cleaned);
            }
            catch (Exception ex)
            {
                var rawPreview = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
                Log.Information("❌ AI media-search JSON parse failed: {Message}", ex.Message);
                Log.Information("❌ AI media-search raw preview: {RawPreview}", rawPreview);
                return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });
            }

            var root = resultDoc.RootElement;

            var isMediaQuery = root.TryGetProperty("isMediaQuery", out var mediaProp) && mediaProp.ValueKind == JsonValueKind.True;
            if (!isMediaQuery)
            {
                var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Query is not about TV shows or movies.";
                return Results.BadRequest(new { error = message ?? "Query is not about TV shows or movies." });
            }

            var summary = root.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null;
            var results = ParseResults(root);

            if (results.Count == 0)
            {
                var retryPrompt = $@"Query: ""{request.Query}""

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
- Keep each blurb concise (max 30 words).";

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
                PerfLog.Record("OpenAI.ChatCompletion", retrySw.Elapsed.TotalMilliseconds, retryResponse.IsSuccessStatusCode, ("Endpoint", "media-search"), ("Model", "gpt-4o"), ("Retry", true));
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
                            retryClean = retryClean.Replace("```json", "").Replace("```", "").Trim();
                        }

                        var retryResultDoc = JsonDocument.Parse(retryClean);
                        var retryRoot = retryResultDoc.RootElement;
                        summary = retryRoot.TryGetProperty("summary", out var retrySummary) ? retrySummary.GetString() : summary;
                        results = ParseResults(retryRoot);
                    }
                }
            }

            return Results.Ok(new AiMediaSearchResponse(summary, results));
        }
        catch (ArgumentException ex)
        {
            Log.Information("❌ Invalid argument for media-search: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("❌ OpenAI media-search failed: {Message}", ex.Message);
            return ApiResponse.InternalError("Failed to run AI media search.");
        }
    }

    private static List<AiMediaSearchItem> ParseResults(JsonElement root)
    {
        var results = new List<AiMediaSearchItem>();
        if (!root.TryGetProperty("results", out var resultsProp) || resultsProp.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in resultsProp.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) continue;

            var year = item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (int?)null;
            var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "tv" : "tv";
            var blurb = item.TryGetProperty("blurb", out var b) ? b.GetString() : null;

            results.Add(new AiMediaSearchItem(title, year, type, blurb));
        }

        return results;
    }
}
