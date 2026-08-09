using System.Diagnostics;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Ai;
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
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection,
        IAiChatCompletion chat,
        CancellationToken cancellationToken)
    {
        if (!listenarr.IsEnabled)
            return ApiResponse.NotFound("Audiobook discovery is not enabled yet.");

        var query = request?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ApiResponse.BadRequest("query is required.");
        if (query.Length > AudiobookDiscoveryPrompt.MaxQueryLength)
            return ApiResponse.BadRequest($"query must be {AudiobookDiscoveryPrompt.MaxQueryLength} characters or fewer.");

        var tokenLimitResult = TokenLimitHelpers.CheckTokenLimit(cfg, tokenUsage, context);
        if (tokenLimitResult is not null) return tokenLimitResult;

        var count = AudiobookDiscoveryPrompt.ClampCount(request!.Count);

        try
        {
            var completion = await CompleteAsync(
                chat, aiResponseParser, context, modelSelection.GetModelDeep(),
                AudiobookDiscoveryPrompt.BuildUserPrompt(query, count),
                temperature: 0.3, isRetry: false, cancellationToken);

            if (completion.Error is not null) return completion.Error;

            if (!completion.IsAudiobookQuery)
            {
                return ApiResponse.BadRequest(completion.Message ?? "That query is not about audiobooks. Try naming a genre, author, or mood.");
            }

            var candidates = completion.Candidates;
            var summary = completion.Summary;

            // One retry, for the single failure worth retrying: valid JSON
            // with an empty list. A parse failure or refusal is returned as-is.
            if (candidates.Count == 0)
            {
                var retry = await CompleteAsync(
                    chat, aiResponseParser, context, "gpt-4o",
                    AudiobookDiscoveryPrompt.BuildRetryPrompt(query, count),
                    temperature: 0.4, isRetry: true, cancellationToken);

                if (retry.Error is null && retry.Candidates.Count > 0)
                {
                    candidates = retry.Candidates;
                    summary ??= retry.Summary;
                }
            }

            if (candidates.Count == 0)
            {
                return ApiResponse.BadRequest("The assistant returned no audiobook suggestions. Try rephrasing the request.");
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
            Log.Warning(ex, "[Listenarr] AI discovery could not reach a dependency");
            return Results.Json(new { error = "Audiobook discovery is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[Listenarr] AI discovery timed out");
            return Results.Json(new { error = "Audiobook discovery timed out. Try a shorter request." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ AI audiobook discovery failed");
            return ApiResponse.InternalError("Failed to run AI audiobook search.");
        }
    }

    private static async Task<AiCompletion> CompleteAsync(
        IAiChatCompletion chat,
        IAiResponseParser aiResponseParser,
        HttpContext context,
        string model,
        string userPrompt,
        double temperature,
        bool isRetry,
        CancellationToken ct)
    {
        var outcome = await chat.CompleteAsync(
            new AiChatCall(
                Endpoint: "audiobook-search",
                Model: model,
                SystemPrompt: AudiobookDiscoveryPrompt.SystemPrompt,
                UserPrompt: userPrompt,
                MaxCompletionTokens: 2500,
                Temperature: temperature,
                IsRetry: isRetry),
            context,
            ct);

        if (!outcome.Succeeded) return AiCompletion.Failed(outcome.Failure!);

        var rawText = outcome.Text;
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
            Log.Information(ex, "❌ AI audiobook-search JSON parse failed after {Length} characters", rawText.Length);
            return AiCompletion.Failed(ApiResponse.BadRequest("The assistant's answer could not be read. Try again or simplify the request."));
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
