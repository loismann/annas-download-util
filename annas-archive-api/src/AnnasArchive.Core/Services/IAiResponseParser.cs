using System.Text.Json;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for parsing OpenAI API responses
/// </summary>
public interface IAiResponseParser
{
    /// <summary>
    /// Extracts text content from OpenAI API response JSON.
    /// Supports both Chat Completions API and Responses API formats.
    /// </summary>
    /// <param name="root">The root JSON element from the API response</param>
    /// <returns>The extracted text content, or null if parsing fails</returns>
    string? ExtractText(JsonElement root);
}
