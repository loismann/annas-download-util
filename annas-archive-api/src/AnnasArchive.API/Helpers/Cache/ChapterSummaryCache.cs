using System.Text.Json;

namespace AnnasArchive.API.Helpers.Cache;

/// <summary>
/// Caching for chapter summaries (regular and ultra).
/// </summary>
public static class ChapterSummaryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetChapterSummaryCachePath(string dropboxPath, int chapterId)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-summaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static string GetUltraChapterSummaryCachePath(string dropboxPath, int chapterId)
    {
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-ultra-summaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static void SaveChapterSummary(string dropboxPath, int chapterId, object summaryData)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        var json = JsonSerializer.Serialize(summaryData, JsonOptions);
        File.WriteAllText(path, json);
        Console.WriteLine($"Saved chapter summary to: {path}");
    }

    public static void SaveUltraChapterSummary(string dropboxPath, int chapterId, object summaryData)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        var json = JsonSerializer.Serialize(summaryData, JsonOptions);
        File.WriteAllText(path, json);
        Console.WriteLine($"Saved ultra chapter summary to: {path}");
    }

    public static T? LoadChapterSummary<T>(string dropboxPath, int chapterId) where T : class
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load chapter summary from {path}: {ex.Message}");
            return null;
        }
    }

    public static T? LoadUltraChapterSummary<T>(string dropboxPath, int chapterId) where T : class
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load ultra chapter summary from {path}: {ex.Message}");
            return null;
        }
    }

    public static bool ChapterSummaryExists(string dropboxPath, int chapterId)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        return File.Exists(path);
    }

    public static bool UltraChapterSummaryExists(string dropboxPath, int chapterId)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        return File.Exists(path);
    }

    public static void DeleteChapterSummary(string dropboxPath, int chapterId)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"Deleted chapter summary: {path}");
        }
    }

    public static void DeleteUltraChapterSummary(string dropboxPath, int chapterId)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"Deleted ultra chapter summary: {path}");
        }
    }

    public static Dictionary<int, Dictionary<string, object>> LoadAllChapterSummaries(string dropboxPath)
    {
        var result = new Dictionary<int, Dictionary<string, object>>();
        var root = AiCacheBase.GetCacheRoot();
        var bookFolder = AiCacheBase.SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-summaries", bookFolder);

        if (!Directory.Exists(dir))
            return result;

        var files = Directory.GetFiles(dir, "chapter-*.json");
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("chapter-") && int.TryParse(fileName.Substring(8), out var chapterId))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var summary = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (summary != null)
                    {
                        result[chapterId] = summary;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load summary from {file}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"Loaded {result.Count} cached summaries for book: {dropboxPath}");
        return result;
    }

    public static List<string> GetAllChapterSummariesAsStrings(string dropboxPath)
    {
        var summaries = new List<string>();
        var chapterSummaries = LoadAllChapterSummaries(dropboxPath);

        foreach (var kvp in chapterSummaries.OrderBy(x => x.Key))
        {
            if (kvp.Value.TryGetValue("summary", out var summaryObj))
            {
                var summaryText = summaryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    summaries.Add(summaryText);
                }
            }
        }

        return summaries;
    }
}
