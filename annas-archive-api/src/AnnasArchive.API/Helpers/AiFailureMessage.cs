using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Turns an AI failure — an HTTP error body or a thrown exception — into one
/// sentence the person in front of the reader can act on.
///
/// Both halves used to be raw passthrough. On the response side the endpoints
/// forwarded the whole error payload; on the exception side they forwarded
/// <c>ex.Message</c>, which after a tripped circuit breaker is Polly's own
/// "The circuit is now open and is not allowing calls." So the sentence OpenAI
/// actually returned — "You have no credits remaining" — reached the log and
/// stopped there, while the browser showed an internal detail naming neither
/// the cause nor the fix.
/// </summary>
public static class AiFailureMessage
{
    public const string Generic = "The AI service is unavailable right now. Please try again.";

    /// <summary>
    /// The <c>error.message</c> the AI service returned, or a status-based
    /// fallback when the body carries no message.
    /// </summary>
    public static string ForResponse(HttpStatusCode status, string? body)
    {
        var provider = ExtractErrorMessage(body);
        if (!string.IsNullOrWhiteSpace(provider))
            return provider;

        return status switch
        {
            HttpStatusCode.TooManyRequests => "The AI service is rate limiting requests. Try again shortly.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The AI service rejected the configured API key.",
            HttpStatusCode.BadRequest => "The AI service rejected the request.",
            _ => $"The AI service returned HTTP {(int)status}."
        };
    }

    /// <summary>
    /// The user-facing sentence for a thrown failure. <see cref="AiServiceException"/>
    /// already carries one — it is built by <see cref="ForResponse"/> at the call
    /// that failed, which is the only place the provider's own wording exists.
    /// </summary>
    public static string ForException(Exception ex) => ex switch
    {
        AiServiceException => ex.Message,
        BrokenCircuitException =>
            "Too many AI requests failed in a row, so calls are paused for a minute. "
            + "Check the OpenAI account's credit balance, then try again.",
        TimeoutRejectedException or TaskCanceledException or TimeoutException =>
            "The AI service took too long to respond.",
        HttpRequestException => "Could not reach the AI service.",
        _ => Generic
    };

    private static string? ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// An AI call that failed with a reason already phrased for the user — thrown by
/// the summary tiers so the streaming endpoint's single catch can surface it
/// without re-deriving what went wrong.
/// </summary>
public sealed class AiServiceException(string userMessage) : Exception(userMessage);
