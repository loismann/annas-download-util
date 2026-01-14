using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper class for GPT-powered chapter labeling in EPUB books.
/// </summary>
public static class ChapterLabelingHelper
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LabelLocks = new();
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Ensures chapters have GPT-generated labels, falling back to heuristic labeling if GPT fails.
    /// </summary>
    public static async Task<CachedChapterIndex> EnsureGptChapterLabelsAsync(
        CachedChapterIndex index,
        string cacheDir,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var model = cfg["OpenAI:ChapterLabelModel"] ?? "gpt-4o";
        if (string.Equals(index.LabelSource, model, StringComparison.OrdinalIgnoreCase) &&
            index.Chapters.All(ch => !string.IsNullOrWhiteSpace(ch.DisplayLabel) && ch.IsMainChapter != null))
        {
            return index;
        }

        var gate = LabelLocks.GetOrAdd(cacheDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var metaPath = Path.Combine(cacheDir, "metadata.json");
            if (File.Exists(metaPath))
            {
                var existingJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
                var cached = JsonSerializer.Deserialize<CachedChapterIndex>(existingJson, CacheJsonOptions);
                if (cached != null &&
                    string.Equals(cached.LabelSource, model, StringComparison.OrdinalIgnoreCase) &&
                    cached.Chapters.All(ch => !string.IsNullOrWhiteSpace(ch.DisplayLabel) && ch.IsMainChapter != null))
                {
                    return cached;
                }
            }

            var labeled = await RequestGptLabelsAsync(index.Chapters, model, httpFactory, cfg, modelHelper, aiResponseParser, cancellationToken);
            if (labeled == null || labeled.Count == 0)
            {
                // Fallback to heuristic labeling when GPT fails
                var fallback = ChapterLabeler.LabelChapters(index.Chapters
                    .Select(ch => new FlatChapter(ch.Id, ch.Title, ch.Level, string.Empty, ch.WordCount))
                    .ToList());

                labeled = fallback.ToDictionary(ch => ch.Chapter.Id, ch => new ChapterLabelResult(
                    ch.Chapter.Id,
                    ch.DisplayLabel,
                    ch.IsMainChapter));
            }

            var updatedChapters = index.Chapters.Select(ch =>
            {
                if (labeled.TryGetValue(ch.Id, out var label) && !string.IsNullOrWhiteSpace(label.DisplayLabel))
                {
                    return ch with { DisplayLabel = label.DisplayLabel, IsMainChapter = label.IsMainChapter };
                }
                return ch;
            }).ToList();

            var updatedIndex = index with { Chapters = updatedChapters, LabelSource = model };
            var metaJson = JsonSerializer.Serialize(updatedIndex, CacheJsonOptions);
            await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken);
            return updatedIndex;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Dictionary<int, ChapterLabelResult>?> RequestGptLabelsAsync(
        IReadOnlyList<CachedChapterMeta> chapters,
        string model,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Information("OpenAI API key not configured for chapter labeling.");
            return null;
        }

        using var http = httpFactory.CreateClient("OpenAI");

        var chapterPayload = chapters.Select(ch => new
        {
            id = ch.Id,
            title = ch.Title,
            wordCount = ch.WordCount
        }).ToList();

        var systemPrompt = @"You label ebook chapter lists. Return ONLY valid JSON, no markdown.
Use the provided chapter titles and word counts to produce a clean display label and whether it's a main chapter.";

        var userPrompt = $@"Input chapters (in reading order):
{JsonSerializer.Serialize(chapterPayload)}

Rules:
- Preserve ids exactly; do not reorder.
- Main chapters should be numbered sequentially: ""Chapter 1: Title"", ""Chapter 2: Title"".
- If no title is provided, use ""Chapter N"" for main chapters.
- Non-chapters (contents, preface, index, maps, acknowledgments, etc.) should use lowercase roman numerals: ""i. Preface"", ""ii. Table of Contents"".
- If a title already contains a chapter number, remove the number and keep the clean title.
- Use wordCount as a hint: very short sections are likely non-chapters.

Return ONLY this JSON array:
[
  {{
    ""id"": 1,
    ""displayLabel"": ""Chapter 1: Title"",
    ""isMainChapter"": true
  }}
]";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 2000,
            temperature: 0.2
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Information($"OpenAI chapter-labeling failed status={(int)response.StatusCode} body={body}");
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var sanitized = SanitizeChapterLabelJson(rawText);
            if (string.IsNullOrWhiteSpace(sanitized))
                return null;

            var parsed = JsonSerializer.Deserialize<List<ChapterLabelResult>>(sanitized, options);
            if (parsed == null || parsed.Count == 0)
                return null;

            return parsed
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex)
        {
            Log.Information($"Failed to parse chapter labels JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sanitizes raw GPT response text to extract valid JSON array.
    /// </summary>
    public static string? SanitizeChapterLabelJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        var trimmed = rawText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');
            if (firstBreak >= 0)
                trimmed = trimmed[(firstBreak + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
                trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)].Trim();

        return trimmed;
    }
}
