using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Video Library metadata management endpoints.
/// Admin-only endpoints for managing video metadata.
/// </summary>
public static class VideoLibraryMetadataEndpoints
{
    /// <summary>
    /// Maps Video Library metadata endpoints to the application.
    /// </summary>
    public static WebApplication MapVideoLibraryMetadataEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/video-library")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        // PATCH /api/video-library/video/{fileName}/metadata - Update video metadata
        group.MapPatch("/video/{fileName}/metadata", HandleUpdateMetadata);

        // PATCH /api/video-library/video/{fileName}/ratings - Update video ratings
        group.MapPatch("/video/{fileName}/ratings", HandleUpdateRatings);

        return app;
    }

    private static async Task<IResult> HandleUpdateMetadata(
        [FromRoute] string fileName,
        [FromBody] VideoMetadataUpdate update,
        VideoIndexCache cache)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var videoRoot = VideoHelpers.ResolveVideoRoot();
        var metaPath = Path.Combine(videoRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = VideoHelpers.CreateVideoJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<VideoMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            // Update fields
            meta.PrimaryGenre = update.PrimaryGenre;
            meta.Tags = update.Tags ?? Array.Empty<string>();
            meta.Playlist = update.Playlist;
            if (!string.IsNullOrWhiteSpace(update.Title))
                meta.Title = update.Title;
            if (!string.IsNullOrWhiteSpace(update.Channel))
                meta.Channel = update.Channel;

            // Save back to file
            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            // Invalidate cache
            cache.InvalidateCache();

            Log.Information("[video-library] Updated metadata for {FileName}: Genre={Genre}, Tags={Tags}, Playlist={Playlist}",
                safeFileName, meta.PrimaryGenre, string.Join(", ", meta.Tags ?? Array.Empty<string>()), meta.Playlist);

            return Results.Ok(new { success = true, message = "Metadata updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[video-library] Invalid argument for metadata update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[video-library] Failed to update metadata for {FileName}", safeFileName);
            return Results.Problem("Failed to update metadata.");
        }
    }

    private static async Task<IResult> HandleUpdateRatings(
        [FromRoute] string fileName,
        [FromBody] VideoRatingsUpdate update,
        VideoIndexCache cache)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var videoRoot = VideoHelpers.ResolveVideoRoot();
        var metaPath = Path.Combine(videoRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = VideoHelpers.CreateVideoJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<VideoMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            if (update.PersonalRating.HasValue)
            {
                var rating = Math.Clamp(update.PersonalRating.Value, 0, 5);
                meta.PersonalRating = rating;
            }

            if (update.Bookmarked.HasValue)
            {
                meta.Bookmarked = update.Bookmarked.Value;
            }

            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            // Invalidate cache
            cache.InvalidateCache();

            Log.Information("[video-library] Updated ratings for {FileName}: Personal={PersonalRating}, Bookmarked={Bookmarked}",
                safeFileName, meta.PersonalRating, meta.Bookmarked);

            return Results.Ok(new { success = true, message = "Ratings updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[video-library] Invalid argument for ratings update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[video-library] Failed to update ratings for {FileName}", safeFileName);
            return Results.Problem("Failed to update ratings.");
        }
    }
}
