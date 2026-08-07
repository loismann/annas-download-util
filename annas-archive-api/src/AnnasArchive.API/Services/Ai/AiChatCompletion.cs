using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using AnnasArchive.Core.Telemetry;
using Serilog;

namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// One chat-completion request: the prompt pair plus the knobs that were tuned
/// alongside it. Token budget and temperature live here rather than at the call
/// site because they are part of the prompt's design — a prompt that asks for
/// "ALL known published titles" needs 3,500 tokens, and moving it without its
/// budget silently truncates the answer.
/// </summary>
/// <param name="Endpoint">Names this call in <c>PerfLog</c>; use the route's own name.</param>
/// <param name="IsRetry">
/// Recorded as a PerfLog tag when set. Null means the endpoint has no retry leg,
/// so the tag is omitted entirely rather than logged as a constant false.
/// </param>
public sealed record AiChatCall(
    string Endpoint,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    int MaxCompletionTokens,
    double Temperature,
    bool? IsRetry = null);

/// <summary>
/// Either the model's text or the <see cref="IResult"/> to return instead.
/// A failure here is already a finished HTTP response — the wording came from
/// <see cref="AiFailureMessage"/>, which knows how to surface the provider's own
/// error sentence rather than a status code.
/// </summary>
public sealed record AiChatOutcome(string? Text, IResult? Failure)
{
    public bool Succeeded => Failure is null;
}

public interface IAiChatCompletion
{
    /// <summary>
    /// Sends one chat completion and bills the caller for it. Returns the
    /// extracted text, or the failure result to return as-is.
    /// </summary>
    Task<AiChatOutcome> CompleteAsync(
        AiChatCall call,
        HttpContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The chat-completion round trip that every AI endpoint was writing out by
/// hand: create the client, build the payload, POST, record the timing, turn a
/// non-2xx into a user-facing failure, extract the text, and charge the tokens
/// to whoever asked.
///
/// Those forty-odd lines were copied per handler, and the copies had drifted —
/// one reported failures as <c>"OpenAI request failed: 429"</c> while the rest
/// surfaced the provider's own message ("You have no credits remaining"), and
/// the token-usage block read <c>usage.prompt_tokens</c> without checking it
/// existed, so a response shaped unexpectedly turned a working answer into a
/// 500. Both are fixed here once, for every caller.
/// </summary>
public sealed class AiChatCompletion(
    IHttpClientFactory httpFactory,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser responseParser,
    ITokenUsageService tokenUsage) : IAiChatCompletion
{
    private const string CompletionsUrl = "https://api.openai.com/v1/chat/completions";

    public async Task<AiChatOutcome> CompleteAsync(
        AiChatCall call,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        using var http = httpFactory.CreateClient("OpenAI");

        var payload = modelHelper.BuildChatCompletionPayload(
            call.Model,
            [
                new { role = "system", content = call.SystemPrompt },
                new { role = "user", content = call.UserPrompt }
            ],
            maxCompletionTokens: call.MaxCompletionTokens,
            temperature: call.Temperature);

        var sw = Stopwatch.StartNew();
        var response = await http.PostAsJsonAsync(CompletionsUrl, payload, cancellationToken);
        PerfLog.Record("OpenAI.ChatCompletion", sw.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode, Tags(call));

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The derived message, not the raw body. The call sites disagreed
            // about this and each had half of it: most logged the whole body,
            // which on a 400 can echo the user's own prompt back into the log;
            // the audiobook one logged status only for exactly that reason, and
            // so dropped the one sentence worth having. What
            // AiFailureMessage extracts *is* the body's actionable content.
            var message = AiFailureMessage.ForResponse(response.StatusCode, body);
            Log.Error("❌ OpenAI {Endpoint} failed with HTTP {StatusCode}: {Reason}",
                call.Endpoint, (int)response.StatusCode, message);
            return new AiChatOutcome(null, Results.Problem(message));
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        RecordUsage(doc.RootElement, context);

        return new AiChatOutcome(responseParser.ExtractText(doc.RootElement), null);
    }

    private static (string Key, object? Value)[] Tags(AiChatCall call) =>
        call.IsRetry is { } retry
            ? [("Endpoint", call.Endpoint), ("Model", call.Model), ("Retry", retry)]
            : [("Endpoint", call.Endpoint), ("Model", call.Model)];

    /// <summary>
    /// Charges the tokens to the signed-in user. Every field is probed rather
    /// than demanded: an unbilled call is a wrong number in a usage report,
    /// while a throw here would discard an answer the account was already
    /// charged for.
    /// </summary>
    private void RecordUsage(JsonElement root, HttpContext context)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        var userId = UserHelpers.GetUserIdFromContext(context);
        if (userId is null)
            return;

        var promptTokens = TokenCount(usage, "prompt_tokens");
        var completionTokens = TokenCount(usage, "completion_tokens");
        if (promptTokens == 0 && completionTokens == 0)
            return;

        tokenUsage.AddUsage(userId, promptTokens, completionTokens);
    }

    private static int TokenCount(JsonElement usage, string property) =>
        usage.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
