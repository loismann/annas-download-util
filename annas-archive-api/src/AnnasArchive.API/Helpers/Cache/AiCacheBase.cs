namespace AnnasArchive.API.Helpers.Cache;

/// <summary>
/// Base utilities for AI content caching.
/// Provides shared path resolution and key sanitization.
/// </summary>
public static class AiCacheBase
{
    /// <summary>
    /// Gets the root directory for AI cache storage.
    /// Uses AI_CACHE_ROOT environment variable or defaults to ./ai-cache.
    /// </summary>
    public static string GetCacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("AI_CACHE_ROOT");
        return env ?? Path.Combine(Directory.GetCurrentDirectory(), "ai-cache");
    }

    /// <summary>
    /// Sanitizes a string to be safe for use as a filename.
    /// </summary>
    public static string SanitizeForFilename(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (sanitized.Length > 200) sanitized = sanitized.Substring(0, 200);
        return sanitized;
    }

    /// <summary>
    /// Public wrapper for filename sanitization.
    /// </summary>
    public static string SanitizeKey(string input) => SanitizeForFilename(input);

    /// <summary>
    /// Gets all existing summary keys across all cache subdirectories.
    /// </summary>
    public static HashSet<string> GetExistingSummaryKeys()
    {
        var root = GetCacheRoot();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subDirs = new[]
        {
            "chapter-summaries",
            "chapter-ultra-summaries",
            "section-summaries",
            "chunk-boundaries",
            "character-graphs"
        };

        foreach (var subDir in subDirs)
        {
            var path = Path.Combine(root, subDir);
            if (!Directory.Exists(path))
                continue;

            foreach (var dir in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(name))
                    keys.Add(name);
            }
        }

        return keys;
    }

    /// <summary>
    /// Checks if any summaries exist for a given key.
    /// </summary>
    public static bool HasAnySummaries(string key, ISet<string> existingKeys)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sanitized = SanitizeKey(key);
        return existingKeys.Contains(sanitized) || existingKeys.Contains(key);
    }

    /// <summary>
    /// Deletes all AI cache directories for a specific book.
    /// </summary>
    public static bool DeleteAllAiCacheForBook(string dropboxPath)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var deletedCount = 0;

        try
        {
            var dirsToDelete = new[]
            {
                ("chapter-summaries", "chapter summaries"),
                ("chunk-boundaries", "chunk boundaries"),
                ("chapter-ultra-summaries", "ultra chapter summaries"),
                ("section-summaries", "section summaries and vocab"),
                ("character-graphs", "character graph")
            };

            foreach (var (subDir, description) in dirsToDelete)
            {
                var dirPath = Path.Combine(root, subDir, bookFolder);
                if (Directory.Exists(dirPath))
                {
                    Directory.Delete(dirPath, recursive: true);
                    deletedCount++;
                    Console.WriteLine($"Deleted {description}: {dirPath}");
                }
            }

            Console.WriteLine($"Deleted {deletedCount} AI cache directories for book");
            return deletedCount > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete AI cache: {ex.Message}");
            return false;
        }
    }
}
