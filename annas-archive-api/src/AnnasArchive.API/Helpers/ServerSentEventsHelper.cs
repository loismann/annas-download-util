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
