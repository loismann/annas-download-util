using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library metadata management endpoints.
/// </summary>
public static class LibraryMetadataEndpoints
{
    /// <summary>
    /// Maps Library metadata endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryMetadataEndpoints(this WebApplication app)
    {
        // PATCH /api/library/book/{fileName}/metadata - Update book metadata
        app.MapPatch("/api/library/book/{fileName}/metadata", HandleUpdateMetadata)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // PATCH /api/library/book/{fileName}/ratings - Update book ratings
        app.MapPatch("/api/library/book/{fileName}/ratings", HandleUpdateRatings)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/{fileName}/reader - Toggle reader inclusion (route param)
        app.MapPost("/api/library/book/{fileName}/reader", HandleToggleReaderByRoute)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/book/reader - Toggle reader inclusion (query param)
        app.MapPost("/api/library/book/reader", HandleToggleReaderByQuery)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/library/books/genres/wipe - Wipe all genres
        app.MapPost("/api/library/books/genres/wipe", HandleWipeGenres)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/library/book/{fileName}/summary - Get/generate book summary
        app.MapGet("/api/library/book/{fileName}/summary", HandleGetSummary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleUpdateMetadata(
        [FromRoute] string fileName,
        [FromBody] LibraryBookMetadataUpdate update,
        HttpContext context)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            // Update fields
            meta.PrimaryGenre = update.PrimaryGenre;
            meta.Tags = update.Tags ?? Array.Empty<string>();
            meta.Series = update.Series;
            if (!string.IsNullOrWhiteSpace(update.Title))
                meta.Title = update.Title;
            if (update.Authors != null)
                meta.Authors = update.Authors;

            // Save back to file
            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            Log.Information("[library] Updated metadata for {safeFileName}: Genre={meta.PrimaryGenre}, Tags={string.Join(\", \", meta.Tags)}, Series={meta.Series}");

            return Results.Ok(new { success = true, message = "Metadata updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[library] Invalid argument for metadata update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update metadata for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update metadata.");
        }
    }

    private static async Task<IResult> HandleUpdateRatings(
        [FromRoute] string fileName,
        [FromBody] LibraryBookRatingsUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            if (update.GoodreadsRating.HasValue)
            {
                var gr = Math.Clamp(update.GoodreadsRating.Value, 0, 5);
                meta.GoodreadsRating = gr;
            }

            if (update.PersonalRating.HasValue)
            {
                var pr = Math.Clamp(update.PersonalRating.Value, 0, 5);
                meta.PersonalRating = pr;
            }

            var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            Log.Information("[library] Updated ratings for {safeFileName}: Goodreads={meta.GoodreadsRating}, Personal={meta.PersonalRating}");

            return Results.Ok(new { success = true, message = "Ratings updated successfully." });
        }
        catch (ArgumentException ex)
        {
            Log.Information("[library] Invalid argument for ratings update: {Message}", ex.Message);
            return Results.BadRequest(new { error = $"Invalid parameter: {ex.ParamName ?? "unknown"}" });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update ratings for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update ratings.");
        }
    }

    private static async Task<IResult> HandleToggleReaderByRoute(
        [FromRoute] string fileName,
        [FromBody] LibraryBookReaderUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            var enabled = update?.Enabled ?? true;
            var updated = meta with { ReaderEnabled = enabled };
            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            return Results.Ok(new { success = true, enabled });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update reader flag.");
        }
    }

    private static async Task<IResult> HandleToggleReaderByQuery(
        [FromQuery] string? fileName,
        [FromBody] LibraryBookReaderUpdate update)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Reader supports EPUB files only." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            var enabled = update?.Enabled ?? true;
            var updated = meta with { ReaderEnabled = enabled };
            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);

            return Results.Ok(new { success = true, enabled });
        }
        catch (Exception ex)
        {
            Log.Information("[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
            return Results.Problem("Failed to update reader flag.");
        }
    }

    private static async Task<IResult> HandleWipeGenres()
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Ok(new { success = true, updated = 0 });

        var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        var updatedCount = 0;

        foreach (var metaPath in metaFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaPath);
                var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null)
                    continue;

                var updated = meta with
                {
                    PrimaryGenre = null,
                    Tags = Array.Empty<string>(),
                    Genres = Array.Empty<string>()
                };

                var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
                await File.WriteAllTextAsync(metaPath, updatedJson);
                updatedCount++;
            }
            catch
            {
                // ignore individual file failures
            }
        }

        return Results.Ok(new { success = true, updated = updatedCount });
    }

    private static async Task<IResult> HandleGetSummary(
        [FromRoute] string fileName,
        IDescriptionFetcherService descriptionFetcher)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid fileName." });

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return Results.NotFound(new { error = "Metadata file not found." });

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

            if (meta == null)
                return Results.BadRequest(new { error = "Invalid metadata file." });

            // If we already have a description, return it
            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                return Results.Ok(new { summary = meta.Description, source = "cached" });
            }

            // Try to fetch from external sources using the centralized service
            var title = meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName);
            var author = meta.Authors?.FirstOrDefault();

            var result = await descriptionFetcher.FetchDescriptionAsync(title, author);
            var summary = result.Description;
            var source = result.Source;

            // Save to metadata if we got a summary
            if (!string.IsNullOrWhiteSpace(summary))
            {
                try
                {
                    // Re-read to avoid race conditions
                    json = await File.ReadAllTextAsync(metaPath);
                    meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                    if (meta != null)
                    {
                        var updated = meta with { Description = summary };
                        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
                        await File.WriteAllTextAsync(metaPath, updatedJson);
                        Log.Information("[library-summary] Saved summary for {safeFileName} (source: {source})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Information("[library-summary] Failed to save summary: {ex.Message}");
                }
            }

            return Results.Ok(new { summary, source });
        }
        catch (Exception ex)
        {
            Log.Information("[library-summary] Error: {ex.Message}");
            return Results.Problem("Failed to get summary.");
        }
    }
}
