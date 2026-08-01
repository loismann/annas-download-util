using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// The judgement calls the library watcher makes about a book's metadata: which
/// files to look at, whether what we have is good enough, whether a filename-derived
/// title beats the one already stored, and how to read loosely-typed values out of
/// AI/JSON responses.
///
/// Split out of <see cref="LibraryWatcherService"/>, which is otherwise a
/// filesystem watcher with a network-bound enrichment pipeline. Everything here is
/// a pure function of its arguments and is directly tested.
/// </summary>
public static class LibraryMetadataRules
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2", ".txt", ".rtf", ".lit", ".djvu"
    };

    /// <summary>Good enough to skip enrichment: a real title, and either no authors
    /// at all or at least one that isn't an initial or a stray character.</summary>
    public static bool IsMetadataReliable(string? title, string[]? authors)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3)
            return false;

        if (authors == null || authors.Length == 0)
            return true;

        return authors.Any(a => !string.IsNullOrWhiteSpace(a) && a.Trim().Length >= 3);
    }

    /// <summary>
    /// Whether a title parsed from the filename should replace the stored one.
    /// Only when the stored title is absent, or is itself obviously filename
    /// debris: identical to the raw base name, containing underscores, or ending
    /// in a long uppercase/digit run (an ISBN, hash or release tag).
    /// </summary>
    public static bool ShouldUseParsedTitle(string? existingTitle, string? parsedTitle, string rawBaseName)
    {
        if (string.IsNullOrWhiteSpace(parsedTitle))
            return false;

        if (string.IsNullOrWhiteSpace(existingTitle))
            return true;

        var normalizedExisting = existingTitle.Trim();

        return string.Equals(normalizedExisting, rawBaseName, StringComparison.OrdinalIgnoreCase)
            || normalizedExisting.Contains('_')
            || Regex.IsMatch(normalizedExisting, @"[A-Z0-9]{8,}$");
    }

    /// <summary>Local covers live under `_covers/` and must never be overwritten by
    /// an external URL — the file on disk is the one the user actually chose.</summary>
    public static bool IsLocalCover(string? coverUrl) =>
        !string.IsNullOrWhiteSpace(coverUrl) &&
        coverUrl.StartsWith("_covers/", StringComparison.OrdinalIgnoreCase);

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
            return "0B";

        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.0}{units[unitIndex]}";
    }

    /// <summary>Accepts a number or a numeric string — AI replies are inconsistent
    /// about quoting, and both spellings mean the same thing here.</summary>
    public static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var num))
            return num;

        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    /// <inheritdoc cref="TryGetDouble"/>
    public static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
            return num;

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    public static object? TryGetMetaValue(Dictionary<string, object?> meta, string key) =>
        meta.TryGetValue(key, out var value) ? value : null;

    public static string[]? TryGetMetaArray(Dictionary<string, object?> meta, string key) =>
        meta.TryGetValue(key, out var value) ? value as string[] : null;

    /// <summary>Writes only when nothing useful is there yet. "Nothing useful"
    /// includes a present-but-blank string and a present-but-empty array, which is
    /// what enrichment actually produces when a lookup comes back empty.</summary>
    public static void SetIfMissing(Dictionary<string, object?> meta, string key, object value)
    {
        if (!meta.TryGetValue(key, out var current) || current == null ||
            (current is string str && string.IsNullOrWhiteSpace(str)) ||
            (current is string[] arr && arr.Length == 0))
        {
            meta[key] = value;
        }
    }

    /// <summary>Digs the assistant's text out of the Responses-API shape:
    /// output[] -> content[] -> first part with a `text` property.</summary>
    public static string? ExtractResponseText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textProp))
                    return textProp.GetString();
            }
        }

        return null;
    }
}
