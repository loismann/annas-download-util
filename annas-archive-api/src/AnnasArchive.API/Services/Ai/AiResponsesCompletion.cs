using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using AnnasArchive.Core.Telemetry;
using Serilog;

namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// One Responses-API request.
///
/// The Responses API is not a stylistic variant of Chat Completions — it takes
/// <c>input</c> instead of <c>messages</c>, <c>max_output_tokens</c> instead of
/// <c>max_completion_tokens</c>, reports <c>input_tokens</c>/<c>output_tokens</c>
/// instead of <c>prompt_tokens</c>/<c>completion_tokens</c>, and carries a
/// <c>reasoning.effort</c> knob that has no Chat Completions equivalent. That is
/// why these call sites could not simply adopt <see cref="IAiChatCompletion"/>.
/// </summary>
/// <param name="SystemPrompt">
/// When set, <c>input</c> is sent as a role/content array; when null, as a plain
/// string. Both forms are in use and the difference is not cosmetic — the array
/// form is what lets a caller separate instructions from the user's text.
/// </param>
/// <param name="ReasoningEffort">
/// Sent only when set. The tiered summary paths tune this per stage, which is
/// most of how a chapter summary is made affordable.
/// </param>
public sealed record AiResponsesCall(
    string Endpoint,
    string Model,
    string Input,
    int MaxOutputTokens,
    string? SystemPrompt = null,
    string? ReasoningEffort = null,
    double? Temperature = null);

/// <summary>What a call cost. Zero when the response carried no usable numbers.</summary>
public sealed record AiUsage(int PromptTokens, int CompletionTokens)
{
    public static readonly AiUsage None = new(0, 0);
}

/// <summary>
/// Either the model's text, or the failure. <see cref="Usage"/> is populated on
/// success so callers that aggregate across several calls — the three-tier
/// summary reports a combined figure to the browser — can do so without
/// re-parsing the response themselves.
/// </summary>
public sealed record AiResponsesOutcome(string? Text, IResult? Failure, AiUsage Usage)
{
    public bool Succeeded => Failure is null;

    /// <summary>The user-facing sentence behind a failure, for callers inside an
    /// SSE stream that must throw rather than return an <see cref="IResult"/>.</summary>
    public string FailureMessage { get; init; } = AiFailureMessage.Generic;
}

public interface IAiResponsesCompletion
{
    Task<AiResponsesOutcome> CompleteAsync(
        AiResponsesCall call, HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// For work with no request behind it. See <see cref="IAiChatCompletion"/>
    /// for why background spend gets its own account rather than the nearest
    /// signed-in person.
    /// </summary>
    Task<AiResponsesOutcome> CompleteAsync(
        AiResponsesCall call, string? billTo, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Responses-API counterpart of <see cref="AiChatCompletion"/>, and it
/// exists for the same reason: eight call sites were writing this round trip out
/// by hand, and the copies had drifted apart in the same three ways — what the
/// user is told when it fails, what reaches the log, and whether the spend is
/// counted at all.
///
/// The token-usage read is the one worth naming. Every hand-written copy did
/// <c>usage.GetProperty("input_tokens").GetInt32()</c> with no check, and the
/// three inside the chapter-summary tiers are in the most expensive path in the
/// application — a response missing that field turns a summary the account was
/// already billed for into a 500.
/// </summary>
public sealed class AiResponsesCompletion(
    IHttpClientFactory httpFactory,
    IAiResponseParser responseParser,
    ITokenUsageService tokenUsage) : IAiResponsesCompletion
{
    private const string ResponsesUrl = "https://api.openai.com/v1/responses";

    public Task<AiResponsesOutcome> CompleteAsync(
        AiResponsesCall call, HttpContext context, CancellationToken cancellationToken = default) =>
        CompleteAsync(call, UserHelpers.GetUserIdFromContext(context), cancellationToken);

    public async Task<AiResponsesOutcome> CompleteAsync(
        AiResponsesCall call, string? billTo, CancellationToken cancellationToken = default)
    {
        using var http = httpFactory.CreateClient("OpenAI");

        var sw = Stopwatch.StartNew();
        var response = await http.PostAsJsonAsync(ResponsesUrl, BuildPayload(call), cancellationToken);
        PerfLog.Record("OpenAI.Responses", sw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode,
            ("Endpoint", call.Endpoint), ("Model", call.Model));

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The derived message, never the raw body: on a 400 the body can
            // echo the prompt, and for these callers the prompt is a chapter of
            // somebody's book.
            var message = AiFailureMessage.ForResponse(response.StatusCode, body);
            Log.Error("❌ OpenAI {Endpoint} failed with HTTP {StatusCode}: {Reason}",
                call.Endpoint, (int)response.StatusCode, message);

            return new AiResponsesOutcome(null, Results.Problem(message), AiUsage.None)
            {
                FailureMessage = message
            };
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        AiSpend.Record(tokenUsage, billTo, doc.RootElement);

        return new AiResponsesOutcome(
            responseParser.ExtractText(doc.RootElement),
            null,
            ReadUsage(doc.RootElement));
    }

    /// <summary>
    /// Optional fields are omitted rather than sent as null. The reasoning
    /// models reject a <c>temperature</c> they did not ask for, so "not set"
    /// and "set to a default" are different requests.
    /// </summary>
    private static object BuildPayload(AiResponsesCall call)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = call.Model,
            ["max_output_tokens"] = call.MaxOutputTokens,
            ["input"] = call.SystemPrompt is null
                ? call.Input
                : new object[]
                {
                    new { role = "system", content = call.SystemPrompt },
                    new { role = "user", content = call.Input }
                }
        };

        if (!string.IsNullOrWhiteSpace(call.ReasoningEffort))
            payload["reasoning"] = new { effort = call.ReasoningEffort };

        if (call.Temperature is { } temperature)
            payload["temperature"] = temperature;

        return payload;
    }

    private static AiUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return AiUsage.None;

        return new AiUsage(Count(usage, "input_tokens"), Count(usage, "output_tokens"));
    }

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
