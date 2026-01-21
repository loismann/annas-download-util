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

    private static IResult HandleListBooks(
        HttpContext context,
        LibraryIndexCache cache,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool? sortDesc = null)
    {
        Log.Information("[library] HandleListBooks called with skip={Skip}, take={Take}, sortBy={SortBy}, sortDesc={SortDesc}",
            skip, take, sortBy, sortDesc);

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        // If pagination parameters are provided, use paginated response
        if (skip.HasValue || take.HasValue)
        {
            var (books, totalCount) = cache.GetBooksPaginated(
                baseUrl,
                skip: skip ?? 0,
                take: take ?? 50,
                sortBy: sortBy ?? "date",
                sortDesc: sortDesc ?? true);

            Log.Information("[library] Returning {Count}/{Total} books (paginated, cached: {IsCached})",
                books.Count, totalCount, cache.IsCached);

            return Results.Json(new
            {
                books,
                totalCount,
                skip = skip ?? 0,
                take = take ?? 50
            });
        }

        // Legacy: return all books for backward compatibility
        var allBooks = cache.GetBooks(baseUrl);
        Log.Information("[library] Returning {Count} books (cached: {IsCached})", allBooks.Count, cache.IsCached);
        return Results.Json(allBooks);
    }

    private static IResult HandleListReaderBooks(HttpContext context, LibraryIndexCache cache)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        var allBooks = cache.GetBooks(baseUrl);

        var existingKeys = AiContentCache.GetExistingSummaryKeys();
        var results = new List<ReaderBookDto>();

        foreach (var book in allBooks)
        {
            // Only include EPUBs
            if (!string.Equals(book.Format, "EPUB", StringComparison.OrdinalIgnoreCase))
                continue;

            var readerKey = ResolveReaderKey(book.FileName, existingKeys);
            var hasSummaries = AiContentCache.HasAnySummaries(readerKey, existingKeys);
            var include = book.ReaderEnabled == true || hasSummaries;

            if (!include)
                continue;

            results.Add(new ReaderBookDto(
                book.FileName,
                readerKey,
                book.Title,
                book.Authors,
                book.Format,
                book.CoverUrl,
                hasSummaries
            ));
        }

        return Results.Json(results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IResult HandleDeleteBook([FromRoute] string fileName, LibraryIndexCache cache)
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

            // Remove from cache immediately (file watcher will also catch this)
            cache.RemoveBook(safeFileName);

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
