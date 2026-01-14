using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Helpers.Cache;

/// <summary>
/// Caching for section summaries, chunk boundaries, and section vocabulary.
/// </summary>
public static class SectionSummaryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    #region Chunk Boundaries

    public static string GetChunkBoundariesCachePath(string dropboxPath, int chapterId)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chunk-boundaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static void SaveChunkBoundaries(string dropboxPath, int chapterId, List<ChunkBoundary> chunks)
    {
        var path = GetChunkBoundariesCachePath(dropboxPath, chapterId);
        var data = new ChunkBoundariesResponse(chapterId, chunks, DateTime.UtcNow);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Saved chunk boundaries to: {Path}", path);
    }

    public static ChunkBoundariesResponse? LoadChunkBoundaries(string dropboxPath, int chapterId)
    {
        var path = GetChunkBoundariesCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ChunkBoundariesResponse>(json);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load chunk boundaries from {Path}: {ErrorMessage}", path, ex.Message);
            return null;
        }
    }

    #endregion

    #region Section Summaries

    public static string GetSectionSummaryCachePath(string dropboxPath, int chapterId, int sectionIndex)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "section-summaries", bookFolder, $"chapter-{chapterId}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"section-{sectionIndex}.json");
    }

    public static void SaveSectionSummary(string dropboxPath, int chapterId, int sectionIndex, object summaryData)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        var json = JsonSerializer.Serialize(summaryData, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Saved section summary to: {Path}", path);
    }

    public static SectionSummaryResponse? LoadSectionSummary(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SectionSummaryResponse>(json);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load section summary from {Path}: {ErrorMessage}", path, ex.Message);
            return null;
        }
    }

    public static bool SectionSummaryExists(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        return File.Exists(path);
    }

    public static List<string> GetAllSectionSummaries(string dropboxPath)
    {
        var summaries = new List<string>();
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var sectionsDir = Path.Combine(root, "section-summaries", bookFolder);

        if (!Directory.Exists(sectionsDir))
            return summaries;

        var chapterDirs = Directory.GetDirectories(sectionsDir)
            .OrderBy(d => d)
            .ToArray();

        foreach (var chapterDir in chapterDirs)
        {
            var sectionFiles = Directory.GetFiles(chapterDir, "section-*.json")
                .OrderBy(f => f)
                .ToArray();

            foreach (var sectionFile in sectionFiles)
            {
                try
                {
                    var json = File.ReadAllText(sectionFile);
                    var summary = JsonSerializer.Deserialize<SectionSummaryResponse>(json);
                    if (summary != null && !string.IsNullOrWhiteSpace(summary.Summary))
                    {
                        summaries.Add(summary.Summary);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to load section summary from {FilePath}: {ErrorMessage}", sectionFile, ex.Message);
                }
            }
        }

        return summaries;
    }

    #endregion

    #region Section Vocabulary

    public static string GetSectionVocabCachePath(string dropboxPath, int chapterId, int sectionIndex)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "section-summaries", bookFolder, $"chapter-{chapterId}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"section-{sectionIndex}-vocab.json");
    }

    public static void SaveSectionVocab(string dropboxPath, int chapterId, int sectionIndex, List<FlashcardItem> vocabCards)
    {
        var path = GetSectionVocabCachePath(dropboxPath, chapterId, sectionIndex);
        var json = JsonSerializer.Serialize(vocabCards, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Saved section vocab to: {Path}", path);
    }

    public static List<FlashcardItem>? LoadSectionVocab(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionVocabCachePath(dropboxPath, chapterId, sectionIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<FlashcardItem>>(json);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load section vocab from {Path}: {ErrorMessage}", path, ex.Message);
            return null;
        }
    }

    #endregion
}
