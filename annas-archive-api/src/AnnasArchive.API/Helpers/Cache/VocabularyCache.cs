using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace AnnasArchive.API.Helpers.Cache;

/// <summary>
/// Caching for vocabulary tracking (known words and study words).
/// </summary>
public static class VocabularyCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    #region Term Normalization

    /// <summary>
    /// Normalize a term for matching: lower-case, trim, collapse punctuation,
    /// strip simple plurals/possessives, and normalize curly quotes.
    /// This MUST match the frontend normalization in vocabulary.service.ts!
    /// </summary>
    public static string NormalizeTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return string.Empty;

        // Convert to lowercase and normalize curly quotes to straight apostrophes
        var normalized = term.ToLowerInvariant()
            .Replace('\u2018', '\'')  // left single quote
            .Replace('\u2019', '\''); // right single quote

        // Replace hyphens with spaces (so "root-book" becomes "root book")
        normalized = normalized.Replace('-', ' ');

        // Remove all punctuation except apostrophes and spaces
        normalized = Regex.Replace(normalized, @"[^a-z0-9'\s]", " ");

        // Collapse multiple spaces
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        // Strip possessives ('s) and simple plurals (s)
        if (normalized.EndsWith("'s"))
        {
            normalized = normalized.Substring(0, normalized.Length - 2);
        }
        else if (normalized.EndsWith('s') && normalized.Length > 3)
        {
            normalized = normalized.Substring(0, normalized.Length - 1);
        }

        return normalized;
    }

    #endregion

    #region Known Words

    public static string GetKnownWordsPath()
    {
        var root = AiCacheBase.GetCacheRoot();
        var dir = Path.Combine(root, "vocabulary");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "known-words.json");
    }

    /// <summary>
    /// Loads known words with their associated book IDs.
    /// Storage format: Dictionary&lt;term, List&lt;bookId&gt;&gt;
    /// </summary>
    public static Dictionary<string, List<string>> LoadKnownWordsWithBooks()
    {
        var path = GetKnownWordsPath();
        if (!File.Exists(path)) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            return data ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load known words from {Path}: {ErrorMessage}", path, ex.Message);
            Log.Information("Deleting incompatible vocabulary file and starting fresh...");
            try
            {
                File.Delete(path);
                Log.Information("Deleted old known-words.json, will create new book-aware format on first save");
            }
            catch (Exception deleteEx)
            {
                Log.Warning("Failed to delete old file: {ErrorMessage}", deleteEx.Message);
            }
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveKnownWordsWithBooks(Dictionary<string, List<string>> knownWords)
    {
        var path = GetKnownWordsPath();
        var json = JsonSerializer.Serialize(knownWords, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Saved {Count} known words to: {Path}", knownWords.Count, path);
    }

    /// <summary>
    /// Helper to get all known terms (for filtering vocab).
    /// </summary>
    public static HashSet<string> LoadKnownWords()
    {
        var data = LoadKnownWordsWithBooks();
        return new HashSet<string>(data.Keys, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Study Words

    public static string GetStudyWordsPath()
    {
        var root = AiCacheBase.GetCacheRoot();
        var dir = Path.Combine(root, "vocabulary");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "study-words.json");
    }

    /// <summary>
    /// Loads study words with their definitions and associated book IDs.
    /// Storage format: Dictionary&lt;term, { definition: string, books: List&lt;string&gt; }&gt;
    /// </summary>
    public static Dictionary<string, (string definition, List<string> books)> LoadStudyWordsWithBooks()
    {
        var path = GetStudyWordsPath();
        if (!File.Exists(path)) return new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, StudyWordEntry>>(json);
            if (data == null) return new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in data)
            {
                result[kvp.Key] = (kvp.Value.Definition ?? "", kvp.Value.Books ?? new List<string>());
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load study words from {Path}: {ErrorMessage}", path, ex.Message);
            Log.Information("Deleting incompatible vocabulary file and starting fresh...");
            try
            {
                File.Delete(path);
                Log.Information("Deleted old study-words.json, will create new book-aware format on first save");
            }
            catch (Exception deleteEx)
            {
                Log.Warning("Failed to delete old file: {ErrorMessage}", deleteEx.Message);
            }
            return new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveStudyWordsWithBooks(Dictionary<string, (string definition, List<string> books)> studyWords)
    {
        var path = GetStudyWordsPath();

        // Convert to serializable format
        var data = new Dictionary<string, StudyWordEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in studyWords)
        {
            data[kvp.Key] = new StudyWordEntry
            {
                Definition = kvp.Value.definition,
                Books = kvp.Value.books
            };
        }

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Saved {Count} study words to: {Path}", studyWords.Count, path);
    }

    #endregion

    #region Internal Types

    private class StudyWordEntry
    {
        public string? Definition { get; set; }
        public List<string>? Books { get; set; }
    }

    #endregion
}
