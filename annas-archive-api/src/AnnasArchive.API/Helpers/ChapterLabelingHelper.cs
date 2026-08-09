using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using AnnasArchive.API.Services.Ai;
using Serilog;

namespace AnnasArchive.API.Helpers;



/// <summary>
/// Helper class for GPT-powered chapter labeling in EPUB books.
/// </summary>
public static class ChapterLabelingHelper
{
    // Refcounted, so a cache directory stops costing a SemaphoreSlim the moment
    // nothing is labelling it. The plain ConcurrentDictionary this replaces kept
    // one per book, forever.
    private static readonly KeyedLocks LabelLocks = new();
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
        IConfiguration cfg,
        IAiResponseParser aiResponseParser,
        IAiChatCompletion chat,
        string? billTo,
        CancellationToken cancellationToken)
    {
        var model = cfg["OpenAI:ChapterLabelModel"] ?? "gpt-4o";
        if (string.Equals(index.LabelSource, model, StringComparison.OrdinalIgnoreCase) &&
            index.Chapters.All(ch => !string.IsNullOrWhiteSpace(ch.DisplayLabel) && ch.IsMainChapter != null))
        {
            return index;
        }

        using var gate = await LabelLocks.AcquireAsync(cacheDir, cancellationToken);
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

            var labeled = await RequestGptLabelsAsync(
                index.Chapters, model, cfg, aiResponseParser, chat, billTo, cancellationToken);
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
    }

    /// <param name="billTo">Owner key charged for the labelling call. It runs
    /// once per book at 2,000 completion tokens and used to record nothing, so
    /// opening a new book in the reader was free as far as the allowance
    /// could see.</param>
    private static async Task<Dictionary<int, ChapterLabelResult>?> RequestGptLabelsAsync(
        IReadOnlyList<CachedChapterMeta> chapters,
        string model,
        IConfiguration cfg,
        IAiResponseParser aiResponseParser,
        IAiChatCompletion chat,
        string? billTo,
        CancellationToken cancellationToken)
    {
        // One source. HttpClientConfiguration reads "OpenAI:ApiKey" and only
        // that key, so a key supplied any other way passes this guard and then
        // fails where the client is built — with no sentence worth reading.
        var apiKey = cfg["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Information("OpenAI API key not configured for chapter labeling.");
            return null;
        }

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

        var outcome = await chat.CompleteAsync(
            new AiChatCall(
                Endpoint: "chapter-labeling",
                Model: model,
                SystemPrompt: systemPrompt,
                UserPrompt: userPrompt,
                MaxCompletionTokens: 2000,
                Temperature: 0.2),
            billTo,
            cancellationToken);

        // A failure here is not fatal: the caller falls back to heuristic
        // labelling, which is why this returns null rather than the outcome's
        // IResult.
        if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.Text))
            return null;

        var rawText = outcome.Text;

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
        catch (ArgumentException ex)
        {
            Log.Information("Invalid argument parsing chapter labels JSON: {ParamName}", ex.ParamName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Failed to parse chapter labels JSON");
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
