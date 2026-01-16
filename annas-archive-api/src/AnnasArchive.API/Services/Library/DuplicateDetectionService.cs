using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for detecting and managing duplicate book files in the library.
/// Uses a two-pass algorithm to identify duplicates by title and title+author.
/// </summary>
public class DuplicateDetectionService : IDuplicateDetectionService
{
    /// <inheritdoc />
    public HashSet<string> FindDuplicates(string libraryRoot, List<string> files)
    {
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var title = GetTitleForDedup(file);
            var normalized = NormalizeTitleForDedup(title);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!byTitle.TryGetValue(normalized, out var list))
            {
                list = new List<string>();
                byTitle[normalized] = list;
            }
            list.Add(file);
        }

        // Pass 1: same-format duplicates by title (safe cleanup).
        foreach (var group in byTitle.Values.Where(list => list.Count > 1))
        {
            var byExt = group
                .GroupBy(path => Path.GetExtension(path).ToLowerInvariant());

            foreach (var extGroup in byExt)
            {
                var ordered = extGroup
                    .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                    .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var path in ordered.Skip(1))
                    duplicates.Add(path);
            }
        }

        // Pass 2: cross-format duplicates only when title + author match.
        var byTitleAuthor = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (duplicates.Contains(file))
                continue;

            var title = NormalizeTitleForDedup(GetTitleForDedup(file));
            var authorKey = NormalizeAuthorForDedup(GetAuthorsForDedup(file));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(authorKey))
                continue;

            var key = $"{title}||{authorKey}";
            if (!byTitleAuthor.TryGetValue(key, out var list))
            {
                list = new List<string>();
                byTitleAuthor[key] = list;
            }
            list.Add(file);
        }

        foreach (var group in byTitleAuthor.Values.Where(list => list.Count > 1))
        {
            var ordered = group
                .OrderBy(path => FormatRank(Path.GetExtension(path)))
                .ThenByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in ordered.Skip(1))
                duplicates.Add(path);
        }

        return duplicates;
    }

    /// <inheritdoc />
    public void DeleteLibraryArtifacts(string libraryRoot, string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);

            var metaPath = $"{filePath}.meta.json";
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            var coverDir = Path.Combine(libraryRoot, "_covers");
            if (Directory.Exists(coverDir))
            {
                var safeName = Path.GetFileName(filePath);
                foreach (var cover in Directory.GetFiles(coverDir, $"{safeName}.cover.*"))
                {
                    try { File.Delete(cover); } catch { }
                }
            }
        }
        catch
        {
            // ignore delete failures
        }
    }

    private static string GetTitleForDedup(string filePath)
    {
        var metaPath = $"{filePath}.meta.json";
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    return titleProp.GetString() ?? "";
            }
            catch
            {
                // ignore
            }
        }

        var parsed = ParseTitleAuthorFromFileName(filePath);
        return parsed.Title ?? Path.GetFileNameWithoutExtension(filePath);
    }

    private static string[] GetAuthorsForDedup(string filePath)
    {
        var metaPath = $"{filePath}.meta.json";
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("authors", out var authorsProp) &&
                    authorsProp.ValueKind == JsonValueKind.Array)
                {
                    var authors = authorsProp.EnumerateArray()
                        .Select(v => v.GetString())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!.Trim())
                        .ToArray();
                    if (authors.Length > 0)
                        return authors;
                }
            }
            catch
            {
                // ignore
            }
        }

        var parsed = ParseTitleAuthorFromFileName(filePath);
        return parsed.Authors ?? Array.Empty<string>();
    }

    private static string NormalizeTitleForDedup(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var normalized = title.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\[[^\]]*\]", "");
        normalized = Regex.Replace(normalized, @"\([^)]+\)", "");
        normalized = Regex.Replace(normalized, @"\bbook\s+\d+\b", "");
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", "");
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();
        return normalized;
    }

    private static string NormalizeAuthorForDedup(string[]? authors)
    {
        if (authors == null || authors.Length == 0)
            return "";

        var normalized = string.Join(";", authors)
            .ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9\s,;]", "");
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();
        return normalized;
    }

    private static int FormatRank(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".epub" => 0,
            ".azw3" => 1,
            ".azw" => 2,
            ".kfx" => 3,
            ".pobi" => 4,
            ".mobi" => 5,
            ".pdf" => 6,
            ".fb2" => 7,
            _ => 8
        };
    }

    private static (string? Title, string[]? Authors) ParseTitleAuthorFromFileName(string filePath)
    {
        var raw = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        var cleaned = Regex.Replace(raw, @"_(?:sample|preview)$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\.(tmp|tmp\d+)_\w+$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"_[A-Z0-9]{8,}$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\bB0[A-Z0-9]{8,}\b$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.Replace('_', ' ').Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

        var separators = new[] { " - ", " – ", " — " };
        foreach (var sep in separators)
        {
            var idx = cleaned.IndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0 || idx >= cleaned.Length - sep.Length)
                continue;

            var left = cleaned[..idx].Trim();
            var right = cleaned[(idx + sep.Length)..].Trim();
            if (left.Length < 3 || right.Length < 3)
                continue;

            var looksLikeAuthor = left.Contains(',') || left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
            if (looksLikeAuthor)
                return (right, new[] { left });
        }

        return (cleaned, null);
    }
}
