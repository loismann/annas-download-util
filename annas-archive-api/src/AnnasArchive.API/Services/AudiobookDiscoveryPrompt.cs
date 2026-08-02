using System.Text.Json;
using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

/// <summary>
/// Pure prompt construction and response parsing for AI audiobook discovery.
/// It is deliberately separate from the endpoint and from every catalog
/// service: the model sees only the household member's own words and a
/// schema, never the Audiobookshelf catalog, the Listenarr library, queue
/// state, indexer responses, or filesystem paths. The privacy regression
/// test asserts exactly that against this class.
/// </summary>
public static class AudiobookDiscoveryPrompt
{
    public const int MinCount = 1;
    public const int MaxCount = 30;
    public const int DefaultCount = 12;
    public const int MaxQueryLength = 2000;
    public const int MaxReasonWords = 40;

    public const string SystemPrompt =
        "You are an audiobook discovery assistant. Decide whether the user query is asking for audiobooks or books. " +
        "If it is, recommend real, published works with a short reason each one fits. " +
        "Return ONLY valid JSON with no markdown and no extra text.";

    public static int ClampCount(int? requested) =>
        requested is null ? DefaultCount : Math.Clamp(requested.Value, MinCount, MaxCount);

    public static string BuildUserPrompt(string query, int count) => $$"""
        Query: "{{query}}"

        Return ONLY this JSON structure:
        {
          "isAudiobookQuery": boolean,
          "message": string|null,
          "summary": string|null,
          "results": [
            {
              "title": "Work title",
              "author": "Author name",
              "year": 2020,
              "series": "Series name or null",
              "seriesNumber": "1",
              "narratorPreference": "Narrator only if the user named one, else null",
              "reason": "Why this fits the query"
            }
          ]
        }

        Rules:
        - If the query is NOT about audiobooks or books, set isAudiobookQuery=false and return a brief message.
        - Return exactly {{count}} results when you can, fewer only if fewer real works fit.
        - Every title and author must be a real published work. Never invent one.
        - Do not return ASINs, ISBNs, URLs, magnet links, NZBs, or edition identifiers of any kind.
        - Set narratorPreference only when the user explicitly asked for a narrator.
        - Make the summary 1-2 sentences explaining what the list represents.
        - Keep each reason under {{MaxReasonWords}} words.
        """;

    /// <summary>Retry prompt for the one case worth retrying: valid JSON with
    /// an empty result list.</summary>
    public static string BuildRetryPrompt(string query, int count) =>
        BuildUserPrompt(query, count) + $"""


        You previously returned no results. You MUST return at least {Math.Min(count, 5)} real works.
        """;

    public static IReadOnlyList<AudiobookDiscoveryCandidate> ParseCandidates(JsonElement root)
    {
        var candidates = new List<AudiobookDiscoveryCandidate>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return candidates;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            candidates.Add(new AudiobookDiscoveryCandidate(
                title,
                Text(item, "author"),
                item.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number
                    ? year.GetInt32()
                    : null,
                Text(item, "series"),
                Text(item, "seriesNumber"),
                Text(item, "narratorPreference"),
                TrimReason(Text(item, "reason"))));
        }

        return candidates;
    }

    public static string? TrimReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var words = reason.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxReasonWords
            ? string.Join(' ', words)
            : string.Join(' ', words.Take(MaxReasonWords)) + "…";
    }

    /// <summary>Reads a string property, treating JSON null and whitespace as
    /// absent so downstream resolution never scores on the literal "null".</summary>
    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }
}
