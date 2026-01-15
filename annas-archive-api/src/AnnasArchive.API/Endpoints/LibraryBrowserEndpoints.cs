using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library browsing endpoints.
/// </summary>
public static class LibraryBrowserEndpoints
{
    /// <summary>
    /// Maps Library browsing endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryBrowserEndpoints(this WebApplication app)
    {
        // GET /api/library/books - List library books
        app.MapGet("/api/library/books", HandleListBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/books - List reader-enabled books
        app.MapGet("/api/library/reader/books", HandleListReaderBooks)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/library/book/{fileName} - Delete book
        app.MapDelete("/api/library/book/{fileName}", HandleDeleteBook)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static IResult HandleListBooks(HttpContext context)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Json(Array.Empty<LibraryBookDto>());

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var books = new List<LibraryBookDto>();
        var metaLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        foreach (var metaFile in metaFiles)
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                metaLookup.Add(meta.FileName);
                var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                    ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

                var genres = meta.Genres ?? Array.Empty<string>();
                var tags = meta.Tags ?? genres;
                var primaryGenre = meta.PrimaryGenre ?? genres.FirstOrDefault() ?? tags.FirstOrDefault();

                books.Add(new LibraryBookDto(
                    meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                    meta.Authors ?? Array.Empty<string>(),
                    meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                    meta.FileSize ?? "",
                    meta.FileName,
                    coverUrl,
                    meta.Source,
                    meta.Md5,
                    meta.SavedAt,
                    primaryGenre,
                    tags,
                    meta.Series,
                    genres,
                    meta.PublishedDate,
                    meta.Pages,
                    meta.GoodreadsRating,
                    meta.PersonalRating,
                    meta.ReaderEnabled
                ));
            }
            catch
            {
                // ignore malformed meta files
            }
        }

        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2" };

        foreach (var filePath in Directory.GetFiles(libraryRoot))
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                if (!supportedExts.Contains(ext))
                    continue;

                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(fileName) || metaLookup.Contains(fileName))
                    continue;

                var info = new FileInfo(filePath);
                books.Add(new LibraryBookDto(
                    Path.GetFileNameWithoutExtension(fileName),
                    Array.Empty<string>(),
                    ext.TrimStart('.').ToUpperInvariant(),
                    LibraryHelpers.FormatFileSize(info.Length),
                    fileName,
                    null,
                    null,
                    null,
                    info.LastWriteTimeUtc,
                    null,
                    Array.Empty<string>(),
                    null,
                    Array.Empty<string>(),
                    null,
                    null,
                    null,
                    null,
                    null
                ));
            }
            catch (Exception ex)
            {
                Log.Information("[library] Skipping file {filePath}: {ex.Message}");
            }
        }

        var ordered = books
            .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Json(ordered);
    }

    private static IResult HandleListReaderBooks(HttpContext context)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Json(Array.Empty<ReaderBookDto>());

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var results = new List<ReaderBookDto>();
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        var existingKeys = AiContentCache.GetExistingSummaryKeys();

        foreach (var metaFile in metaFiles)
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                var ext = Path.GetExtension(meta.FileName);
                if (!string.Equals(ext, ".epub", StringComparison.OrdinalIgnoreCase))
                    continue;

                var readerKey = ResolveReaderKey(meta.FileName, existingKeys);
                var hasSummaries = AiContentCache.HasAnySummaries(readerKey, existingKeys);
                var include = meta.ReaderEnabled == true || hasSummaries;
                if (!include)
                    continue;

                var coverUrl = LibraryHelpers.NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                    ?? LibraryHelpers.FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

                results.Add(new ReaderBookDto(
                    meta.FileName,
                    readerKey,
                    meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                    meta.Authors ?? Array.Empty<string>(),
                    meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                    coverUrl,
                    hasSummaries
                ));
            }
            catch
            {
                // ignore malformed meta files
            }
        }

        return Results.Json(results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IResult HandleDeleteBook([FromRoute] string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var bookPath = Path.Combine(libraryRoot, safeFileName);
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");
        var coverDir = Path.Combine(libraryRoot, "_covers");
        var coverMatches = Directory.Exists(coverDir)
            ? Directory.GetFiles(coverDir, $"{safeFileName}.cover.*")
            : Array.Empty<string>();

        if (!File.Exists(bookPath) && !File.Exists(metaPath) && coverMatches.Length == 0)
            return Results.NotFound(new { error = "Book not found." });

        try
        {
            if (File.Exists(bookPath))
                File.Delete(bookPath);

            if (File.Exists(metaPath))
                File.Delete(metaPath);

            foreach (var cover in coverMatches)
            {
                try { File.Delete(cover); } catch { /* ignore */ }
            }

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to delete book {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to delete book.");
        }
    }

    // Helper function for resolving reader keys
    private static string ResolveReaderKey(string fileName, ISet<string> existingKeys)
    {
        if (existingKeys == null || existingKeys.Count == 0)
            return fileName;

        var sanitized = AiContentCache.SanitizeKey(fileName);
        if (existingKeys.Contains(sanitized))
            return sanitized;

        var match = existingKeys.FirstOrDefault(key =>
            key.EndsWith(sanitized, StringComparison.OrdinalIgnoreCase));
        return match ?? fileName;
    }
}
