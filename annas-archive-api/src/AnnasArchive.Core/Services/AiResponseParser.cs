using System.Text.Json;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for parsing OpenAI API responses
/// </summary>
public class AiResponseParser : IAiResponseParser
{
    /// <summary>
    /// Extracts text content from OpenAI API response JSON.
    /// Supports both Chat Completions API and Responses API formats.
    /// </summary>
    public string? ExtractText(JsonElement root)
    {
        try
        {
            // Try Chat Completions API format first (choices[0].message.content)
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }
            }

            // Fallback to Responses API format (output array)
            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array &&
                output.GetArrayLength() > 0)
            {
                // Responses API structure: output array contains reasoning + message items
                // Find the message type item
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeElem) &&
                        typeElem.GetString() == "message" &&
                        item.TryGetProperty("content", out var contentArray) &&
                        contentArray.ValueKind == JsonValueKind.Array &&
                        contentArray.GetArrayLength() > 0)
                    {
                        // Look for output_text type in content array
                        foreach (var contentItem in contentArray.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty("type", out var contentType) &&
                                contentType.GetString() == "output_text" &&
                                contentItem.TryGetProperty("text", out var textElem) &&
                                textElem.ValueKind == JsonValueKind.String)
                            {
                                return textElem.GetString();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore parse errors, return null
        }
        return null;
    }
}
