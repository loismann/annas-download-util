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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        //
        // Two groups off the same prefix. The "media" bucket is the looser limiter
        // for per-tile requests — a library grid renders hundreds at once and would
        // otherwise exhaust the whole "api" window on covers alone.
        var group = app.MapGroup("/api/library")
            .RequireAuthorization()
            .RequireRateLimiting("api");
        var mediaGroup = app.MapGroup("/api/library")
            .RequireAuthorization()
            .RequireRateLimiting("media");

        // GET /api/library/books - List library books
        group.MapGet("/books", HandleListBooks);

        // GET /api/library/books/search - Search and filter library books (optimized for large libraries)
        group.MapGet("/books/search", HandleSearchBooks);

        // GET /api/library/reader/books - List reader-enabled books
        group.MapGet("/reader/books", HandleListReaderBooks);

        // DELETE /api/library/book/{fileName} - Delete book
        group.MapDelete("/book/{fileName}", HandleDeleteBook);

        // GET /api/library/book/{fileName}/file - Stream a book's raw file bytes (PDF viewer only, for now)
        mediaGroup.MapGet("/book/{fileName}/file", HandleGetBookFile);

        // GET /api/library/download-progress/{jobId} - Poll a "send to library" background download
        // (jobId comes back from either the Anna's Archive or LibGen send-to-library endpoints)
        group.MapGet("/download-progress/{jobId}", HandleGetDownloadProgress);

        return app;
    }

    private static IResult HandleGetDownloadProgress(
        [FromRoute] string jobId,
        Services.IBookDownloadJobService jobs)
    {
        var job = jobs.Get(jobId);
        if (job == null)
            return ApiResponse.NotFound("Job not found.");

        double? percent = job.TotalBytes is > 0
            ? Math.Round(job.BytesDownloaded * 100.0 / job.TotalBytes.Value, 1)
            : null;

        return Results.Json(new
        {
            jobId = job.JobId,
            status = job.Status.ToString().ToLowerInvariant(),
            bytesDownloaded = job.BytesDownloaded,
            totalBytes = job.TotalBytes,
            percent,
            fileName = job.FileName,
            message = job.Message
        });
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

    private static IResult HandleDeleteBook(
        [FromRoute] string fileName,
        LibraryIndexCache cache,
        Data.BookPersonalizationStore personalization)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return ApiResponse.BadRequest("Invalid fileName.");

        try
        {
            var result = LibraryBookDeletionHelper.DeleteBookCompletely(safeFileName, cache, personalization);
            if (!result.Found)
                return ApiResponse.NotFound("Book not found.");

            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[library] Failed to delete book {SafeFileName}", safeFileName);
            return Results.Problem("Failed to delete book.");
        }
    }

    private static IResult HandleGetBookFile([FromRoute] string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return ApiResponse.BadRequest("Invalid fileName.");

        // Only PDFs are viewable in-app for now — other formats go out via
        // Kindle/Dropbox instead, so there's no browser-side renderer for them.
        if (!string.Equals(Path.GetExtension(safeFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Only PDF files can be viewed in-app.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return ApiResponse.NotFound("File not found.");

        return Results.File(fullPath, "application/pdf", enableRangeProcessing: true);
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
