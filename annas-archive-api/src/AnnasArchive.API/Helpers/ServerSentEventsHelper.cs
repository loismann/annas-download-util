using System.Text.Json;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for sending Server-Sent Events (SSE) responses.
/// </summary>
public static class ServerSentEventsHelper
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Declares the response an event stream. Call once, before the first
    /// <see cref="SendEventAsync"/>; headers cannot be set once the body has
    /// started.
    /// </summary>
    ///
    /// <remarks>
    /// <para>The same three headers were set at three call sites, in three
    /// different ways, and one of them was wrong in a way that only luck was
    /// covering:</para>
    ///
    /// <code>
    /// response.ContentType = "text/event-stream";                       // fine
    /// response.Headers["Content-Type"] = "text/event-stream";           // fine, long-hand
    /// response.Headers.Append("Content-Type", "text/event-stream");     // not the same thing
    /// </code>
    ///
    /// <para><c>Append</c> adds to a header rather than replacing it.
    /// <c>Content-Type</c> is single-valued, so appending to one that is already
    /// set yields <c>application/json, text/event-stream</c> and the browser's
    /// <c>EventSource</c> refuses the stream. It happens to work today only
    /// because nothing sets a content type on that path first — which is a
    /// property of the code around it, not of the call.</para>
    ///
    /// <para>Setting <see cref="HttpResponse.ContentType"/> is assignment, so it
    /// is correct however many times it runs and whatever ran before it.</para>
    /// </remarks>
    public static void BeginStream(HttpResponse response)
    {
        response.ContentType = "text/event-stream";

        // Assignment, not Append, for the same reason — and typed accessors so a
        // header name cannot be misspelled into silence.
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
    }

    /// <summary>
    /// Sends an SSE event to the response stream.
    /// </summary>
    /// <param name="response">The HTTP response to write to.</param>
    /// <param name="data">The data object to serialize as JSON.</param>
    /// <param name="eventName">Optional event name for the SSE event.</param>
    public static async Task SendEventAsync(HttpResponse response, object data, string? eventName = null)
    {
        if (eventName is not null)
        {
            await response.WriteAsync($"event: {eventName}\n");
        }

        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
    }
}
