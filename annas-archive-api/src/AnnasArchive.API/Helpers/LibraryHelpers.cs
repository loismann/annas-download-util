using System.Security.Claims;
using System.Text.Json;
using AnnasArchive.API.Models;

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
    /// Resolves a user-specific library tag based on the authenticated user's name.
    /// </summary>
    public static string? ResolveUserLibraryTag(HttpContext context)
    {
        var name = context.User?.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim().ToLowerInvariant();

        // Map user names to tags to avoid storing access codes in code/config.
        if (normalized.Contains("paul"))
            return "Paul's Books";

        if (normalized.Contains("mom"))
            return "Mom's Books";

        if (normalized.Contains("dad"))
            return "Dad's Books";

        return null;
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
}
