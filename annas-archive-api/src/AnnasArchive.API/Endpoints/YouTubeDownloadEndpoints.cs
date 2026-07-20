using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping YouTube download endpoints.
/// Admin-only endpoints for downloading YouTube videos.
/// </summary>
public static class YouTubeDownloadEndpoints
{
    /// <summary>
    /// Maps YouTube download endpoints to the application (admin only).
    /// </summary>
    public static WebApplication MapYouTubeDownloadEndpoints(this WebApplication app)
    {
        var youtubeGroup = app.MapGroup("/api/youtube")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // GET /api/youtube/formats?url=... - Get video info and available formats
        youtubeGroup.MapGet("/formats", HandleGetFormats);

        // POST /api/youtube/download/start - Start a download
        youtubeGroup.MapPost("/download/start", HandleStartDownload);

        // GET /api/youtube/download/{jobId}/status - Get download status
        youtubeGroup.MapGet("/download/{jobId}/status", HandleGetStatus);

        // GET /api/youtube/download/{jobId}/stream - SSE stream for progress
        youtubeGroup.MapGet("/download/{jobId}/stream", HandleStreamProgress);

        // POST /api/youtube/download/{jobId}/cancel - Cancel a download
        youtubeGroup.MapPost("/download/{jobId}/cancel", HandleCancelDownload);

        // GET /api/youtube/downloads - List all downloads
        youtubeGroup.MapGet("/downloads", HandleListDownloads);

        // DELETE /api/youtube/downloads/{jobId} - Delete a download
        youtubeGroup.MapDelete("/downloads/{jobId}", HandleDeleteDownload);

        return app;
    }

    private static async Task<IResult> HandleGetFormats(
        string url,
        IYouTubeDownloadService downloadService,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "URL is required." });

        if (!IsValidYouTubeUrl(url))
            return Results.BadRequest(new { error = "Invalid YouTube URL." });

        try
        {
            var info = await downloadService.GetVideoInfoAsync(url, token);
            Log.Information("[YouTube] Fetched info for: {Title}", info.Title);
            return Results.Ok(info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[YouTube] Failed to get formats for URL: {Url}", url);
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> HandleStartDownload(
        StartDownloadRequest request,
        IYouTubeDownloadService downloadService,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.Url))
            return Results.BadRequest(new { error = "URL is required." });

        if (string.IsNullOrWhiteSpace(request.FormatId))
            return Results.BadRequest(new { error = "Format ID is required." });

        if (!IsValidYouTubeUrl(request.Url))
            return Results.BadRequest(new { error = "Invalid YouTube URL." });

        try
        {
            var jobId = await downloadService.StartDownloadAsync(request, token);
            Log.Information("[YouTube] Started download job: {JobId}", jobId);
            return Results.Ok(new { jobId });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[YouTube] Failed to start download");
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IResult HandleGetStatus(
        string jobId,
        IYouTubeDownloadService downloadService)
    {
        var job = downloadService.GetJobStatus(jobId);
        return job == null
            ? Results.NotFound(new { error = "Job not found." })
            : Results.Ok(job);
    }

    private static async Task HandleStreamProgress(
        string jobId,
        HttpContext context,
        IYouTubeDownloadService downloadService,
        CancellationToken token)
    {
        var job = downloadService.GetJobStatus(jobId);
        if (job == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = "Job not found." }, token);
            return;
        }

        context.Response.Headers.Append("Content-Type", "text/event-stream");
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        try
        {
            await foreach (var progress in downloadService.StreamProgressAsync(jobId, token))
            {
                var eventName = progress.Status is "complete" or "failed" or "cancelled"
                    ? "complete"
                    : null;

                await ServerSentEventsHelper.SendEventAsync(context.Response, progress, eventName);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, that's fine
        }
    }

    private static IResult HandleCancelDownload(
        string jobId,
        IYouTubeDownloadService downloadService)
    {
        var success = downloadService.CancelJob(jobId);
        return success
            ? Results.Ok(new { message = "Download cancelled." })
            : Results.BadRequest(new { error = "Cannot cancel job. It may not exist or is already complete." });
    }

    private static IResult HandleListDownloads(
        IYouTubeDownloadService downloadService)
    {
        var jobs = downloadService.GetAllJobs();
        return Results.Ok(jobs);
    }

    private static IResult HandleDeleteDownload(
        string jobId,
        IYouTubeDownloadService downloadService)
    {
        var success = downloadService.DeleteJob(jobId);
        return success
            ? Results.Ok(new { message = "Download deleted." })
            : Results.NotFound(new { error = "Job not found." });
    }

    private static bool IsValidYouTubeUrl(string url)
    {
        // Accept youtube.com, youtu.be, and various YouTube URL formats
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }
}
