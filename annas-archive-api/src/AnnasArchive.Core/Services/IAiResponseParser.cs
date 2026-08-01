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

    /// <summary>
    /// Strips the markdown code fence that models wrap JSON in even when asked
    /// not to, returning the payload inside.
    /// </summary>
    /// <remarks>
    /// Seventeen call sites each had their own copy of this, in three variants.
    /// The most common one was <c>Replace("```json", "").Replace("```", "")</c>,
    /// which is subtly wrong: it rewrites those sequences anywhere in the
    /// string, including inside JSON string values, so a summary that quoted a
    /// code fence got silently corrupted. This implementation only removes the
    /// opening and closing fences.
    /// </remarks>
    string StripCodeFences(string? text);
}
