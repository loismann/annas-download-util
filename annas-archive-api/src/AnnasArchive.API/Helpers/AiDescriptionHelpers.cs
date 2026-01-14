using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for AI-powered book description generation.
/// </summary>
public static class AiDescriptionHelpers
{
    /// <summary>
    /// Generates a brief, spoiler-free description for a book using AI.
    /// </summary>
    public static async Task<string> GenerateNoSpoilerDescriptionAsync(
        string title,
        string author,
        HttpClient http,
        string model,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser)
    {
        try
        {
            var systemPrompt = "You are a literary assistant. Generate brief, spoiler-free book descriptions.";
            var userPrompt = $@"Generate a single-sentence, no-spoiler description (max 15 words) for:
""{title}"" by {author}

Focus on genre, themes, and general premise without revealing plot details or twists.";

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: 50,
                temperature: 0.5
            );

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                return ""; // Return empty string if API call fails
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var description = aiResponseParser.ExtractText(doc.RootElement)?.Trim() ?? "";

            // Remove quotes if the LLM wrapped the description in quotes
            if (description.StartsWith("\"") && description.EndsWith("\""))
            {
                description = description[1..^1];
            }

            return description;
        }
        catch (Exception ex)
        {
            Log.Warning("[GPT-4] Error generating description: {ErrorMessage}", ex.Message);
            return "";
        }
    }
}
