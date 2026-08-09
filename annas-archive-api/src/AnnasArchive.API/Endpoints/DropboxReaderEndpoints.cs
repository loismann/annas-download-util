using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Dropbox EPUB reader endpoints.
/// </summary>
public static class DropboxReaderEndpoints
{
    /// <summary>
    /// Maps Dropbox EPUB reader endpoints to the application.
    /// </summary>
    public static WebApplication MapDropboxReaderEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/anna/dropbox")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/anna/dropbox/epubs - List EPUBs in Dropbox
        group.MapGet("/epubs", HandleListEpubs);

        // GET /api/anna/dropbox/epub/chapters - Get chapter index for an EPUB
        group.MapGet("/epub/chapters", HandleGetChapters);

        // GET /api/anna/dropbox/epub/chapter - Get single chapter content
        group.MapGet("/epub/chapter", HandleGetChapter);

        // GET /api/anna/dropbox/epub/status - Get cache status for an EPUB
        group.MapGet("/epub/status", HandleGetStatus);

        // POST /api/anna/dropbox/epub/index - Start indexing an EPUB
        group.MapPost("/epub/index", HandleStartIndexing);

        // DELETE /api/anna/dropbox/epub/index - Delete cache for an EPUB
        group.MapDelete("/epub/index", HandleDeleteIndex);

        // GET /api/anna/dropbox/epub/search - Search within an EPUB
        group.MapGet("/epub/search", HandleSearch);

        return app;
    }

    private static async Task<IResult> HandleListEpubs(
        DropboxClient dropbox,
        IConfiguration cfg)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        try
        {
            var folderPath = cfg["Dropbox:UploadFolderPath"] ?? string.Empty;
            var epubs = await DropboxEpubCache.ListDropboxEpubsAsync(dropbox, folderPath);
            return Results.Ok(epubs);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to list Dropbox EPUBs");
            return ApiResponse.InternalError("Unable to list Dropbox files right now.");
        }
    }

    private static async Task<IResult> HandleGetChapters(
        [FromQuery] string? path,
        IValidationService validation,
        DropboxClient dropbox,
        IConfiguration cfg,
        IAiResponseParser aiResponseParser,
        IAiChatCompletion chat,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        if (!validation.IsValidDropboxPath(path))
            return ApiResponse.BadRequest("Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters.");

        try
        {
            var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
            index = await ChapterLabelingHelper.EnsureGptChapterLabelsAsync(
                index,
                cacheDir,
                cfg,
                aiResponseParser,
                chat,
                UserHelpers.GetUserIdFromContext(context),
                cancellationToken);

            var response = new DropboxEpubChaptersResponse(
                index.Title,
                index.Chapters
                    .Where(ch => ch.WordCount >= 50)
                    .Select(ch => new DropboxChapterDto(
                        ch.Id,
                        ch.Title,
                        ch.Level,
                        ch.WordCount,
                        ch.DisplayLabel,
                        ch.IsMainChapter))
                    .ToList()
            );

            return Results.Ok(response);
        }
        catch (ApiException<DownloadError> ex)
        {
            Log.Error("❌ Dropbox download failed: {ExErrorResponse}", ex.ErrorResponse);
            return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to load EPUB");
            return ApiResponse.InternalError("Unable to read the EPUB file.");
        }
    }

    private static async Task<IResult> HandleGetChapter(
        [FromQuery] string? path,
        [FromQuery] int? chapterId,
        IValidationService validation,
        DropboxClient dropbox)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        if (!validation.IsValidDropboxPath(path))
            return ApiResponse.BadRequest("Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters.");

        if (!chapterId.HasValue || !validation.IsValidChapterId(chapterId.Value))
            return ApiResponse.BadRequest("Chapter ID is required and must be between 0 and 9999.");

        try
        {
            var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
            var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId.Value);

            if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
                return ApiResponse.NotFound("Chapter not found.");

            var chapterPath = Path.Combine(cacheDir, chapter.FileName);
            if (!File.Exists(chapterPath))
            {
                await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
            }

            var contentText = await File.ReadAllTextAsync(chapterPath);

            var content = new DropboxChapterContentDto(
                chapter.Id,
                chapter.Title,
                contentText,
                contentText.Length,
                chapter.WordCount);

            return Results.Ok(content);
        }
        catch (ApiException<DownloadError> ex)
        {
            Log.Error("❌ Dropbox download failed: {ExErrorResponse}", ex.ErrorResponse);
            return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to load EPUB");
            return ApiResponse.InternalError("Unable to read the EPUB file.");
        }
    }

    private static async Task<IResult> HandleGetStatus(
        [FromQuery] string? path,
        DropboxClient dropbox)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Only .epub files are supported.");

        try
        {
            var status = await DropboxEpubCache.GetCacheStatusAsync(dropbox, path);
            return Results.Ok(status);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to read cache status");
            return ApiResponse.InternalError("Unable to fetch cache status.");
        }
    }

    private static async Task<IResult> HandleStartIndexing(
        [FromQuery] string? path,
        DropboxClient dropbox)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Only .epub files are supported.");

        try
        {
            var (_, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
            // fire and forget rebuild to ensure freshness
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
            return Results.Ok(new { started = true });
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to start indexing");
            return ApiResponse.InternalError("Unable to start indexing for this book.");
        }
    }

    private static IResult HandleDeleteIndex([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Only .epub files are supported.");

        try
        {
            // Delete EPUB cache (chapter content)
            var epubRemoved = DropboxEpubCache.DeleteCache(path);

            // Delete AI cache (summaries, vocab, chunk boundaries, character graph)
            var aiRemoved = AiContentCache.DeleteAllAiCacheForBook(path);

            Log.Information("🗑️ Cache deletion: EPUB={EpubRemoved}, AI={AiRemoved}", epubRemoved, aiRemoved);
            return Results.Ok(new { epubCacheRemoved = epubRemoved, aiCacheRemoved = aiRemoved });
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to delete cache");
            return ApiResponse.InternalError("Unable to delete cache for this book.");
        }
    }

    private static async Task<IResult> HandleSearch(
        [FromQuery] string? path,
        [FromQuery] string? query,
        DropboxClient dropbox)
    {
        if (dropbox == null)
            return Results.StatusCode(503); // Service unavailable - Dropbox not configured

        if (string.IsNullOrWhiteSpace(path))
            return ApiResponse.BadRequest("Query parameter 'path' is required.");

        if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Only .epub files are supported.");

        if (string.IsNullOrWhiteSpace(query) || query.Length < 10)
            return ApiResponse.BadRequest("Search query must be at least 10 characters.");

        // Validate query max length
        var queryValidation = ValidationHelpers.ValidateStringLength(query, "query", 500);
        if (queryValidation != null)
            return queryValidation;

        try
        {
            var matches = await DropboxEpubCache.SearchAsync(dropbox, path, query);
            return Results.Ok(matches);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Failed to search EPUB cache");
            return ApiResponse.InternalError("Unable to search this book right now.");
        }
    }
}
