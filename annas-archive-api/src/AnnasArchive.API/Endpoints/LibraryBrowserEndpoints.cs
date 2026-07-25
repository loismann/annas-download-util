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

        // GET /api/library/books/search - Search and filter library books (optimized for large libraries)
        app.MapGet("/api/library/books/search", HandleSearchBooks)
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

    /// <summary>
    /// Optimized search endpoint for large libraries.
    /// All filtering, sorting, and pagination happens server-side.
    /// Clients should use this endpoint with infinite scroll for best performance.
    /// </summary>
    private static IResult HandleSearchBooks(
        HttpContext context,
        LibraryIndexCache cache,
        [FromQuery] string? q = null,
        [FromQuery] string? genre = null,
        [FromQuery] string? ownerTags = null,
        [FromQuery] int minPersonalRating = 0,
        [FromQuery] double minGoodreadsRating = 0,
        [FromQuery] bool favoritesOnly = false,
        [FromQuery] bool? missingAuthor = null,
        [FromQuery] bool? missingCover = null,
        [FromQuery] int? genreCountLessThan = null,
        [FromQuery] int? genreCountMoreThan = null,
        [FromQuery] string sortBy = "date",
        [FromQuery] bool sortDesc = true,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        // Parse owner tags (comma-separated)
        string[]? ownerTagsArray = null;
        if (!string.IsNullOrWhiteSpace(ownerTags))
        {
            ownerTagsArray = ownerTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var (books, totalCount, availableGenres) = cache.SearchBooks(
            baseUrl,
            searchTerm: q,
            genre: genre,
            ownerTags: ownerTagsArray,
            minPersonalRating: minPersonalRating,
            minGoodreadsRating: minGoodreadsRating,
            favoritesOnly: favoritesOnly,
            missingAuthor: missingAuthor,
            missingCover: missingCover,
            genreCountLessThan: genreCountLessThan,
            genreCountMoreThan: genreCountMoreThan,
            sortBy: sortBy,
            sortDesc: sortDesc,
            skip: skip,
            take: take);

        Log.Debug("[library-search] q={Query}, genre={Genre}, sort={SortBy}, returning {Count}/{Total}",
            q, genre, sortBy, books.Count, totalCount);

        return Results.Json(new
        {
            books,
            totalCount,
            skip,
            take,
            genres = availableGenres
        });
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

        try
        {
            var result = LibraryBookDeletionHelper.DeleteBookCompletely(safeFileName, cache);
            if (!result.Found)
                return Results.NotFound(new { error = "Book not found." });

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Log.Warning("[library] Failed to delete book {SafeFileName}: {Message}", safeFileName, ex.Message);
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
