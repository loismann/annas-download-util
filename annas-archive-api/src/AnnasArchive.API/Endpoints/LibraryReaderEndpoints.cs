using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library reader (EPUB) endpoints.
/// </summary>
public static class LibraryReaderEndpoints
{
    /// <summary>
    /// Maps Library reader endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryReaderEndpoints(this WebApplication app)
    {
        // GET /api/library/reader/epub/chapters - Get EPUB chapters
        app.MapGet("/api/library/reader/epub/chapters", HandleGetReaderChapters)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/chapter - Get single chapter content
        app.MapGet("/api/library/reader/epub/chapter", HandleGetReaderChapter)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/status - Get cache status
        app.MapGet("/api/library/reader/epub/status", HandleGetReaderStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/reader/epub/index - Start indexing
        app.MapPost("/api/library/reader/epub/index", HandleStartReaderIndexing)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // DELETE /api/library/reader/epub/index - Delete cache
        app.MapDelete("/api/library/reader/epub/index", HandleDeleteReaderIndex)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/reader/epub/search - Search within EPUB
        app.MapGet("/api/library/reader/epub/search", HandleReaderSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleGetReaderChapters(
        [FromQuery] string? fileName,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
        index = await ChapterLabelingHelper.EnsureGptChapterLabelsAsync(
            index,
            cacheDir,
            httpFactory,
            cfg,
            modelHelper,
            aiResponseParser,
            cancellationToken);
        var response = new DropboxEpubChaptersResponse(
            index.Title,
            index.Chapters.Select(ch => new DropboxChapterDto(
                ch.Id,
                ch.Title,
                ch.Level,
                ch.WordCount,
                ch.DisplayLabel,
                ch.IsMainChapter)).ToList());
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleGetReaderChapter(
        [FromQuery] string? fileName,
        [FromQuery] int chapterId,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
        var chapter = index.Chapters.FirstOrDefault(ch => ch.Id == chapterId);
        if (chapter == null)
            return Results.NotFound(new { error = "Chapter not found." });

        var contentPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(contentPath))
        {
            _ = LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
            var fallback = await LibraryEpubCache.ReadChapterContentCachedAsync(fullPath, chapterId);
            if (fallback == null)
                return Results.NotFound(new { error = "Chapter content not ready yet." });

            try
            {
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(contentPath, fallback);
            }
            catch (Exception ex)
            {
                Log.Information("[library] Failed to persist chapter cache for {safeFileName} chapter {chapterId}: {ex.Message}");
            }

            var fallbackResponse = new DropboxChapterContentDto(
                chapter.Id,
                chapter.Title,
                fallback,
                chapter.CharacterCount,
                chapter.WordCount);
            return Results.Ok(fallbackResponse);
        }

        var content = await File.ReadAllTextAsync(contentPath);
        var response = new DropboxChapterContentDto(chapter.Id, chapter.Title, content, chapter.CharacterCount, chapter.WordCount);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleGetReaderStatus([FromQuery] string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var status = await LibraryEpubCache.GetCacheStatusAsync(fullPath, readerKey);
        return Results.Ok(status);
    }

    private static async Task<IResult> HandleStartReaderIndexing([FromBody] LibraryReaderIndexRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var fileName = Path.GetFileName(request.FileName);
        if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var fullPath = Path.Combine(libraryRoot, fileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());
        var (_, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, readerKey);
        await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
        return Results.Ok(new { started = true });
    }

    private static IResult HandleDeleteReaderIndex([FromBody] LibraryReaderIndexRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var fileName = Path.GetFileName(request.FileName);
        if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());

        // Delete EPUB chapter cache
        var epubCacheRemoved = LibraryEpubCache.DeleteCache(readerKey);
        Log.Information("[library] EPUB cache deleted for {ReaderKey}: {Removed}", readerKey, epubCacheRemoved);

        // Delete ALL AI-generated content (summaries, character graphs, section summaries, etc.)
        var aiCacheRemoved = AiContentCache.DeleteAllAiCacheForBook(readerKey);
        Log.Information("[library] AI cache deleted for {ReaderKey}: {Removed}", readerKey, aiCacheRemoved);

        return Results.Ok(new { success = epubCacheRemoved || aiCacheRemoved });
    }

    private static async Task<IResult> HandleReaderSearch(
        [FromQuery] string? fileName,
        [FromQuery] string? query,
        [FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "fileName is required." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });
        var fullPath = Path.Combine(libraryRoot, safeFileName);
        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Book file not found." });

        var normalizedQuery = (query ?? q)?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 10)
            return Results.BadRequest(new { error = "Search query must be at least 10 characters." });

        // Validate query max length
        var queryValidation = ValidationHelpers.ValidateStringLength(normalizedQuery, "query", 500);
        if (queryValidation != null)
            return queryValidation;

        var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
        var results = await LibraryEpubCache.SearchAsync(fullPath, readerKey, normalizedQuery);
        return Results.Ok(results);
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
