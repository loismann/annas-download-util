using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnnasArchive.API.Services;

/// <summary>
/// Sonarr/Radarr report request failures either as a single JSON object or an
/// array of field-level validation errors — this pulls out the human-readable
/// message so it can be surfaced to the user instead of a raw JSON blob or a
/// generic "rejected the request".
/// </summary>
internal static class ArrErrorParsing
{
    public static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "no details returned";

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray array)
            {
                var messages = array
                    .Select(item => item?["errorMessage"]?.ToString() ?? item?["message"]?.ToString())
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                var joined = string.Join("; ", messages);
                if (!string.IsNullOrWhiteSpace(joined))
                    return joined;
            }
            else if (node is JsonObject obj)
            {
                var message = obj["message"]?.ToString() ?? obj["errorMessage"]?.ToString();
                if (!string.IsNullOrWhiteSpace(message))
                    return message;
            }
        }
        catch (JsonException)
        {
            // Body wasn't JSON — fall through to the raw-text fallback below.
        }

        return body.Length > 300 ? body[..300] : body;
    }
}
