using System.Security.Claims;
using System.Text.Json;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for library-related operations.
/// </summary>
public static class LibraryHelpers
{
    /// <summary>
    /// Resolves the root directory path for the book library.
    /// Checks LIBRARY_ROOT env var, then Synology default, then app directory.
    /// </summary>
    public static string ResolveLibraryRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot;

        const string synologyDefault = "/volume1/books/Library";
        if (Directory.Exists(synologyDefault))
            return synologyDefault;

        return Path.Combine(AppContext.BaseDirectory, "library");
    }

    /// <summary>
    /// Resolves which of the three household users ("Paul"/"Mom"/"Dad") is
    /// currently authenticated, by matching a substring of the JWT's Name
    /// claim — avoids storing access codes in code/config.
    /// </summary>
    public static string? ResolveUserDisplayName(HttpContext context)
    {
        var name = context.User?.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim().ToLowerInvariant();

        if (normalized.Contains("paul"))
            return "Paul";

        if (normalized.Contains("mom"))
            return "Mom";

        if (normalized.Contains("dad"))
            return "Dad";

        return null;
    }

    /// <summary>
    /// Resolves a user-specific library tag based on the authenticated user's name.
    /// </summary>
    public static string? ResolveUserLibraryTag(HttpContext context)
    {
        var name = ResolveUserDisplayName(context);
        return name is null ? null : $"{name}'s Books";
    }

    /// <summary>
    /// Creates JSON serializer options for library metadata files.
    /// </summary>
    public static JsonSerializerOptions CreateLibraryJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Writes library metadata for a book to a JSON file.
    /// </summary>
    public static async Task WriteLibraryMetadataAsync(
        string libraryRoot,
        string fileName,
        string md5,
        string? title,
        string? authors,
        string? format,
        string? fileSize,
        string? coverUrl,
        string? source,
        string? userTag,
        string? description)
    {
        var metaPath = Path.Combine(libraryRoot, $"{fileName}.meta.json");
        var authorList = string.IsNullOrWhiteSpace(authors)
            ? Array.Empty<string>()
            : authors.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tags = string.IsNullOrWhiteSpace(userTag)
            ? Array.Empty<string>()
            : new[] { userTag };

        var meta = new LibraryBookMeta(
            title ?? Path.GetFileNameWithoutExtension(fileName),
            authorList,
            format,
            fileSize,
            fileName,
            coverUrl,
            source,
            md5,
            DateTime.UtcNow,
            null,
            tags,
            null,
            Array.Empty<string>(),
            null,
            null,
            null,
            null,
            null,
            description
        );

        var jsonOptions = CreateLibraryJsonOptions();
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, jsonOptions));
    }

    /// <summary>
    /// Gets the library tag for a Kindle target user.
    /// </summary>
    public static string GetKindleTargetTag(string target)
    {
        return target.ToLower() == "mom" ? "Mom's Books" : "Dad's Books";
    }

    /// <summary>
    /// Normalizes a cover URL, converting relative paths to API URLs.
    /// </summary>
    public static string? NormalizeLibraryCoverUrl(string? coverValue, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(coverValue))
            return null;

        if (coverValue.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return coverValue;

        var normalized = coverValue.Replace("\\", "/").TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var encodedPath = string.Join("/", segments);

        return $"{baseUrl}/api/library/cover/{encodedPath}";
    }

    /// <summary>
    /// Finds a local cover file URL for a book in the library.
    /// </summary>
    public static string? FindLocalCoverUrl(string libraryRoot, string fileName, string baseUrl)
    {
        var coverDir = Path.Combine(libraryRoot, "_covers");
        if (!Directory.Exists(coverDir))
            return null;

        var safeName = Path.GetFileName(fileName);
        var matches = Directory.GetFiles(coverDir, $"{safeName}.cover.*");
        var match = matches.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(match))
            return null;

        var relative = Path.Combine("_covers", Path.GetFileName(match)).Replace("\\", "/");
        return NormalizeLibraryCoverUrl(relative, baseUrl);
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
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

    /// <summary>
    /// Adds tags to a library book's metadata file.
    /// </summary>
    public static async Task AddTagsToLibraryBookAsync(string libraryRoot, string fileName, params string[] tagsToAdd)
    {
        if (tagsToAdd == null || tagsToAdd.Length == 0)
            return;

        var metaPath = Path.Combine(libraryRoot, fileName + ".meta.json");
        if (!File.Exists(metaPath))
        {
            Log.Information("[AddTags] Metadata file not found for {fileName}, skipping tag addition");
            return;
        }

        try
        {
            var jsonOptions = CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
            {
                Log.Information("[AddTags] Failed to deserialize metadata for {fileName}");
                return;
            }

            // Get existing tags and add new ones (avoid duplicates)
            var existingTags = new HashSet<string>(meta.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var tagsAdded = false;

            foreach (var tag in tagsToAdd.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                if (existingTags.Add(tag))
                {
                    tagsAdded = true;
                    Log.Information("[AddTags] Adding tag '{tag}' to {fileName}");
                }
            }

            if (tagsAdded)
            {
                meta.Tags = existingTags.ToArray();
                var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
                await File.WriteAllTextAsync(metaPath, updatedJson);
                Log.Information("[AddTags] Successfully updated tags for {fileName}: {string.Join(", ", meta.Tags)}");
            }
            else
            {
                Log.Information("[AddTags] No new tags to add for {fileName}");
            }
        }
        catch (ArgumentException ex)
        {
            Log.Information("[AddTags] Invalid argument adding tags to {fileName}: {ex.ParamName}");
        }
        catch (Exception ex)
        {
            Log.Information("[AddTags] Error adding tags to {fileName}: {ex.Message}");
        }
    }
}
