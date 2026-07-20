using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Video Library browsing endpoints.
/// Admin-only endpoints for browsing the video library.
/// </summary>
public static class VideoLibraryBrowserEndpoints
{
    /// <summary>
    /// Maps Video Library browsing endpoints to the application.
    /// </summary>
    public static WebApplication MapVideoLibraryBrowserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/video-library")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // GET /api/video-library/videos - List videos
        group.MapGet("/videos", HandleListVideos);

        // GET /api/video-library/thumbnail/{*path} - Serve thumbnail
        group.MapGet("/thumbnail/{*path}", HandleServeThumbnail);

        // DELETE /api/video-library/video/{fileName} - Delete video
        group.MapDelete("/video/{fileName}", HandleDeleteVideo);

        // GET /api/video-library/video/{fileName}/stream - Stream video with range support
        group.MapGet("/video/{fileName}/stream", HandleStreamVideo);

        return app;
    }

    private static IResult HandleListVideos(
        HttpContext context,
        VideoIndexCache cache,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool? sortDesc = null)
    {
        Log.Information("[video-library] HandleListVideos called with skip={Skip}, take={Take}, sortBy={SortBy}, sortDesc={SortDesc}",
            skip, take, sortBy, sortDesc);

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        // If pagination parameters are provided, use paginated response
        if (skip.HasValue || take.HasValue)
        {
            var (videos, totalCount) = cache.GetVideosPaginated(
                baseUrl,
                skip: skip ?? 0,
                take: take ?? 50,
                sortBy: sortBy ?? "date",
                sortDesc: sortDesc ?? true);

            Log.Information("[video-library] Returning {Count}/{Total} videos (paginated, cached: {IsCached})",
                videos.Count, totalCount, cache.IsCached);

            return Results.Json(new
            {
                videos,
                totalCount,
                skip = skip ?? 0,
                take = take ?? 50
            });
        }

        // Return all videos
        var allVideos = cache.GetVideos(baseUrl);
        Log.Information("[video-library] Returning {Count} videos (cached: {IsCached})", allVideos.Count, cache.IsCached);
        return Results.Json(allVideos);
    }

    private static IResult HandleServeThumbnail([FromRoute] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "Path is required." });

        // Sanitize path to prevent directory traversal
        var safePath = Path.GetFileName(path);
        if (!string.Equals(path, safePath, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid path." });

        var videoRoot = VideoHelpers.ResolveVideoRoot();
        var fullPath = Path.Combine(videoRoot, safePath);

        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "Thumbnail not found." });

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };

        return Results.File(fullPath, contentType);
    }

    private static IResult HandleDeleteVideo([FromRoute] string fileName, VideoIndexCache cache)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var videoRoot = VideoHelpers.ResolveVideoRoot();
        var videoPath = Path.Combine(videoRoot, safeFileName);
        var metaPath = Path.Combine(videoRoot, safeFileName + ".meta.json");

        // Find associated thumbnail
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var thumbnailExtensions = new[] { ".jpg", ".jpeg", ".webp", ".png" };
        var thumbnailPath = thumbnailExtensions
            .Select(ext => Path.Combine(videoRoot, baseName + ext))
            .FirstOrDefault(File.Exists);

        if (!File.Exists(videoPath) && !File.Exists(metaPath) && thumbnailPath == null)
            return Results.NotFound(new { error = "Video not found." });

        try
        {
            if (File.Exists(videoPath))
                File.Delete(videoPath);

            if (File.Exists(metaPath))
                File.Delete(metaPath);

            if (thumbnailPath != null && File.Exists(thumbnailPath))
            {
                try { File.Delete(thumbnailPath); } catch { /* ignore */ }
            }

            // Remove from cache immediately
            cache.RemoveVideo(safeFileName);

            Log.Information("[video-library] Deleted video {FileName}", safeFileName);
            return Results.Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[video-library] Failed to delete video {FileName}", safeFileName);
            return Results.Problem("Failed to delete video.");
        }
    }

    private static async Task HandleStreamVideo(HttpContext context, [FromRoute] string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid fileName." });
            return;
        }

        var videoRoot = VideoHelpers.ResolveVideoRoot();
        var videoPath = Path.Combine(videoRoot, safeFileName);

        if (!File.Exists(videoPath))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = "Video not found." });
            return;
        }

        var fileInfo = new FileInfo(videoPath);
        var fileLength = fileInfo.Length;
        var ext = Path.GetExtension(videoPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".m4v" => "video/x-m4v",
            _ => "video/mp4"
        };

        // Handle range requests for video seeking
        var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeSpec = rangeHeader["bytes=".Length..];
            var rangeParts = rangeSpec.Split('-');
            var start = long.Parse(rangeParts[0]);
            var end = rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1])
                ? long.Parse(rangeParts[1])
                : fileLength - 1;

            // Limit chunk size to 10MB for better streaming
            var maxChunkSize = 10 * 1024 * 1024L;
            if (end - start + 1 > maxChunkSize)
            {
                end = start + maxChunkSize - 1;
            }

            if (end >= fileLength)
                end = fileLength - 1;

            var contentLength = end - start + 1;

            context.Response.StatusCode = 206; // Partial Content
            context.Response.ContentType = contentType;
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{fileLength}";
            context.Response.ContentLength = contentLength;

            await using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[64 * 1024]; // 64KB buffer
            var remaining = contentLength;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), context.RequestAborted);
                if (bytesRead == 0)
                    break;

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), context.RequestAborted);
                remaining -= bytesRead;
            }
        }
        else
        {
            // Full file request
            context.Response.ContentType = contentType;
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.ContentLength = fileLength;

            await context.Response.SendFileAsync(videoPath, context.RequestAborted);
        }
    }
}
