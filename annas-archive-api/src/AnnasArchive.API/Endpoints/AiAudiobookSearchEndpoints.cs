using System.Diagnostics;
using System.Text.Json;
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
/// AI audiobook discovery — the same model-selection, token-accounting, and
/// JSON-parsing infrastructure as the TV/movie and book search endpoints,
/// with one deliberate difference: suggestions are resolved against the real
/// Audible catalog server-side before they are returned. TV/movie resolution
/// is left to the browser because Sonarr/Radarr lookups are cheap and their
/// results are unambiguous; an audiobook work has many editions, and only a
/// specific ASIN may ever be requested, so resolution belongs next to the
/// deterministic matcher in AudiobookDiscoveryService.
///
/// The model never receives the Audiobookshelf catalog, the Listenarr
/// library, queue state, or filesystem paths — see AudiobookDiscoveryPrompt.
/// </summary>
public static class AiAudiobookSearchEndpoints
{
    public static WebApplication MapAiAudiobookSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/ai/audiobook-search", HandleAudiobookSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleAudiobookSearch(
        HttpContext context,
        [FromBody] AiAudiobookSearchRequest request,
        IListenarrService listenarr,
        AudiobookDiscoveryService discovery,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        CancellationToken cancellationToken)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook discovery is not enabled yet." });

        var query = request?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { error = "query is required." });
        if (query.Length > AudiobookDiscoveryPrompt.MaxQueryLength)
            return Results.BadRequest(new
            {
                error = $"query must be {AudiobookDiscoveryPrompt.MaxQueryLength} characters or fewer."
            });

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        var count = AudiobookDiscoveryPrompt.ClampCount(request!.Count);

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");
            var model = modelSelection.GetModelDeep();

            var completion = await CompleteAsync(
                http, modelHelper, aiResponseParser, tokenUsage, context, model,
                AudiobookDiscoveryPrompt.BuildUserPrompt(query, count),
                maxCompletionTokens: 2500, temperature: 0.3, isRetry: false, cancellationToken);

            if (completion.Error is not null) return completion.Error;

            if (!completion.IsAudiobookQuery)
            {
                return Results.BadRequest(new
                {
                    error = completion.Message ?? "That query is not about audiobooks. Try naming a genre, author, or mood."
                });
            }

            var candidates = completion.Candidates;
            var summary = completion.Summary;

            // One retry, for the single failure worth retrying: valid JSON
            // with an empty list. A parse failure or refusal is returned as-is.
            if (candidates.Count == 0)
            {
                var retry = await CompleteAsync(
                    http, modelHelper, aiResponseParser, tokenUsage, context, "gpt-4o",
                    AudiobookDiscoveryPrompt.BuildRetryPrompt(query, count),
                    maxCompletionTokens: 2500, temperature: 0.4, isRetry: true, cancellationToken);

                if (retry.Error is null && retry.Candidates.Count > 0)
                {
                    candidates = retry.Candidates;
                    summary ??= retry.Summary;
                }
            }

            if (candidates.Count == 0)
            {
                return Results.BadRequest(new
                {
                    error = "The assistant returned no audiobook suggestions. Try rephrasing the request."
                });
            }

            var resolveStarted = Stopwatch.GetTimestamp();
            var resolved = await discovery.ResolveAsync(
                summary, candidates.Take(count).ToList(), request.Region, cancellationToken);

            Log.Information(
                "[Listenarr] AI discovery resolved {Suggested} suggestions: {Resolved} exact, {Ambiguous} ambiguous, {NotFound} unmatched, {Owned} already owned, elapsed {ElapsedMs}ms",
                candidates.Count, resolved.ResolvedCount, resolved.AmbiguousCount,
                resolved.NotFoundCount, resolved.OwnedCount,
                Stopwatch.GetElapsedTime(resolveStarted).TotalMilliseconds);

            return Results.Ok(resolved);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] AI discovery could not reach a dependency: {Message}", ex.Message);
            return Results.Json(new { error = "Audiobook discovery is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] AI discovery timed out: {Message}", ex.Message);
            return Results.Json(new { error = "Audiobook discovery timed out. Try a shorter request." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            Log.Information("❌ AI audiobook discovery failed: {Message}", ex.Message);
            return ApiResponse.InternalError("Failed to run AI audiobook search.");
        }
    }

    private static async Task<AiCompletion> CompleteAsync(
        HttpClient http,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        ITokenUsageService tokenUsage,
        HttpContext context,
        string model,
        string userPrompt,
        int maxCompletionTokens,
        double temperature,
        bool isRetry,
        CancellationToken ct)
    {
        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = AudiobookDiscoveryPrompt.SystemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens,
            temperature);

        var sw = Stopwatch.StartNew();
        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, ct);
        PerfLog.Record("OpenAI.ChatCompletion", sw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode,
            ("Endpoint", "audiobook-search"), ("Model", model), ("Retry", isRetry));

        if (!response.IsSuccessStatusCode)
        {
            // Status only: the response body can echo the prompt back.
            Log.Information("❌ OpenAI audiobook-search failed status={StatusCode}", (int)response.StatusCode);
            return AiCompletion.Failed(Results.Problem($"OpenAI request failed: {(int)response.StatusCode}"));
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var userId = UserHelpers.GetUserIdFromContext(context);
            if (userId is not null)
            {
                tokenUsage.AddUsage(
                    userId,
                    usage.GetProperty("prompt_tokens").GetInt32(),
                    usage.GetProperty("completion_tokens").GetInt32());
            }
        }

        if (string.IsNullOrWhiteSpace(rawText))
            return AiCompletion.Failed(Results.Problem("AI audiobook search returned an empty response."));

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(AiText.StripCodeFences(rawText));
        }
        catch (JsonException ex)
        {
            // Length only, never the body: it contains the user's own words.
            Log.Information(
                "❌ AI audiobook-search JSON parse failed after {Length} characters: {Message}",
                rawText.Length, ex.Message);
            return AiCompletion.Failed(Results.BadRequest(new
            {
                error = "The assistant's answer could not be read. Try again or simplify the request."
            }));
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            var isAudiobookQuery = root.TryGetProperty("isAudiobookQuery", out var flag) &&
                flag.ValueKind == JsonValueKind.True;

            return new AiCompletion(
                null,
                isAudiobookQuery,
                root.TryGetProperty("message", out var message) ? message.GetString() : null,
                root.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                isAudiobookQuery ? AudiobookDiscoveryPrompt.ParseCandidates(root) : []);
        }
    }

    private sealed record AiCompletion(
        IResult? Error,
        bool IsAudiobookQuery,
        string? Message,
        string? Summary,
        IReadOnlyList<AudiobookDiscoveryCandidate> Candidates)
    {
        public static AiCompletion Failed(IResult error) => new(error, false, null, null, []);
    }
}
