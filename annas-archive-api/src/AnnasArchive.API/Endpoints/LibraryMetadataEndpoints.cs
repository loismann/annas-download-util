using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping Library metadata management endpoints.
///
/// All user edits write to <see cref="BookPersonalizationStore"/> (SQLite), never
/// to the .meta.json sidecars — the sidecars are enrichment-owned and are only
/// read here as fallback values. This is the write-side half of the §8.6 fix:
/// the enrichment watcher and user edits no longer share a file, so neither can
/// clobber the other.
/// </summary>
public static class LibraryMetadataEndpoints
{
    /// <summary>
    /// Maps Library metadata endpoints to the application.
    /// </summary>
    public static WebApplication MapLibraryMetadataEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/library")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // PATCH /api/library/book/{fileName}/metadata - Update book metadata
        group.MapPatch("/book/{fileName}/metadata", HandleUpdateMetadata);

        // PATCH /api/library/book/{fileName}/ratings - Update book ratings
        group.MapPatch("/book/{fileName}/ratings", HandleUpdateRatings);

        // POST /api/library/book/{fileName}/reader - Toggle reader inclusion (route param)
        group.MapPost("/book/{fileName}/reader", HandleToggleReaderByRoute);

        // POST /api/library/book/reader - Toggle reader inclusion (query param)
        group.MapPost("/book/reader", HandleToggleReaderByQuery);

        // POST /api/library/books/genres/wipe - Wipe all genres
        group.MapPost("/books/genres/wipe", HandleWipeGenres);

        // GET /api/library/book/{fileName}/summary - Get/generate book summary
        group.MapGet("/book/{fileName}/summary", HandleGetSummary);

        // POST /api/library/book/{fileName}/favorite - Toggle favorite for the logged-in user
        group.MapPost("/book/{fileName}/favorite", HandleSetFavorite);

        return app;
    }

    /// <summary>Resolves and validates the route/query fileName; null result means invalid.</summary>
    private static string? SafeName(string? fileName)
    {
        var safe = Path.GetFileName(fileName);
        return !string.IsNullOrWhiteSpace(fileName) && string.Equals(fileName, safe, StringComparison.Ordinal)
            ? safe
            : null;
    }

    /// <summary>A book "exists" if either the ebook file or its enrichment sidecar is present —
    /// personalization must also work for orphan files the watcher hasn't processed yet.</summary>
    private static bool BookExists(string libraryRoot, string safeFileName) =>
        File.Exists(Path.Combine(libraryRoot, safeFileName)) ||
        File.Exists(Path.Combine(libraryRoot, safeFileName + ".meta.json"));

    private static async Task<LibraryBookMeta?> TryReadMetaAsync(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<LibraryBookMeta>(json, LibraryHelpers.CreateLibraryJsonOptions());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IResult> HandleSetFavorite(
        [FromRoute] string fileName,
        [FromBody] LibraryBookFavoriteUpdate update,
        HttpContext context,
        BookPersonalizationStore store,
        LibraryIndexCache cache)
    {
        var safeFileName = SafeName(fileName);
        if (safeFileName == null)
            return ApiResponse.BadRequest("Invalid fileName.");

        // Who's favoriting is resolved from the authenticated session, not a client-supplied
        // value — the same reasoning as the Kindle-send tag fix: never trust the client to say
        // who they are when that identity determines what gets written.
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner == null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!BookExists(libraryRoot, safeFileName))
            return ApiResponse.NotFound("Book not found.");

        try
        {
            // First favorite for a book starts from whatever the enrichment sidecar
            // already recorded (pre-store history), so nothing is lost in the handoff.
            var meta = await TryReadMetaAsync(Path.Combine(libraryRoot, safeFileName + ".meta.json"));

            var row = store.Update(safeFileName, p =>
            {
                var favoritedBy = new HashSet<string>(
                    p.FavoritedBy ?? meta?.FavoritedBy ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                if (update.Favorited)
                    favoritedBy.Add(owner);
                else
                    favoritedBy.Remove(owner);

                p.FavoritedBy = favoritedBy.ToArray();
            });

            cache.InvalidateCache();
            return Results.Ok(new { success = true, favoritedBy = row.FavoritedBy });
        }
        catch (Exception ex)
        {
            Log.Warning("[library] Failed to update favorite for {SafeFileName}: {Message}", safeFileName, ex.Message);
            return Results.Problem("Failed to update favorite.");
        }
    }

    private static IResult HandleUpdateMetadata(
        [FromRoute] string fileName,
        [FromBody] LibraryBookMetadataUpdate update,
        BookPersonalizationStore store,
        LibraryIndexCache cache)
    {
        var safeFileName = SafeName(fileName);
        if (safeFileName == null)
            return ApiResponse.BadRequest("Invalid fileName.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!BookExists(libraryRoot, safeFileName))
            return ApiResponse.NotFound("Book not found.");

        try
        {
            store.Update(safeFileName, p =>
            {
                // Empty string = "explicitly cleared" (distinct from null = no opinion) —
                // see BookPersonalization's override semantics.
                p.PrimaryGenre = update.PrimaryGenre ?? "";
                p.Tags = update.Tags ?? Array.Empty<string>();
                p.Series = update.Series ?? "";
                if (!string.IsNullOrWhiteSpace(update.Title))
                    p.Title = update.Title;
                if (update.Authors != null)
                    p.Authors = update.Authors;
            });

            cache.InvalidateCache();

            Log.Information("[library] Updated metadata for {FileName}: Genre={Genre}, Tags={Tags}, Series={Series}",
                safeFileName, update.PrimaryGenre, string.Join(", ", update.Tags ?? Array.Empty<string>()), update.Series);

            return Results.Ok(new { success = true, message = "Metadata updated successfully." });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[library] Failed to update metadata for {SafeFileName}", safeFileName);
            return Results.Problem("Failed to update metadata.");
        }
    }

    private static IResult HandleUpdateRatings(
        [FromRoute] string fileName,
        [FromBody] LibraryBookRatingsUpdate update,
        BookPersonalizationStore store,
        LibraryIndexCache cache)
    {
        var safeFileName = SafeName(fileName);
        if (safeFileName == null)
            return ApiResponse.BadRequest("Invalid fileName.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!BookExists(libraryRoot, safeFileName))
            return ApiResponse.NotFound("Book not found.");

        try
        {
            var row = store.Update(safeFileName, p =>
            {
                if (update.GoodreadsRating.HasValue)
                    p.GoodreadsRating = Math.Clamp(update.GoodreadsRating.Value, 0, 5);
                if (update.PersonalRating.HasValue)
                    p.PersonalRating = Math.Clamp(update.PersonalRating.Value, 0, 5);
            });

            cache.InvalidateCache();

            Log.Information("[library] Updated ratings for {FileName}: Goodreads={Goodreads}, Personal={Personal}",
                safeFileName, row.GoodreadsRating, row.PersonalRating);

            return Results.Ok(new { success = true, message = "Ratings updated successfully." });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[library] Failed to update ratings for {SafeFileName}", safeFileName);
            return Results.Problem("Failed to update ratings.");
        }
    }

    private static IResult HandleToggleReaderByRoute(
        [FromRoute] string fileName,
        [FromBody] LibraryBookReaderUpdate update,
        BookPersonalizationStore store,
        LibraryIndexCache cache) =>
        SetReaderFlag(fileName, update, store, cache, requireEpub: false);

    private static IResult HandleToggleReaderByQuery(
        [FromQuery] string? fileName,
        [FromBody] LibraryBookReaderUpdate update,
        BookPersonalizationStore store,
        LibraryIndexCache cache) =>
        SetReaderFlag(fileName, update, store, cache, requireEpub: true);

    private static IResult SetReaderFlag(
        string? fileName,
        LibraryBookReaderUpdate update,
        BookPersonalizationStore store,
        LibraryIndexCache cache,
        bool requireEpub)
    {
        var safeFileName = SafeName(fileName);
        if (safeFileName == null)
            return ApiResponse.BadRequest("Invalid fileName.");

        if (requireEpub && !string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.BadRequest("Reader supports EPUB files only.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!BookExists(libraryRoot, safeFileName))
            return ApiResponse.NotFound("Book not found.");

        try
        {
            var enabled = update?.Enabled ?? true;
            store.Update(safeFileName, p => p.ReaderEnabled = enabled);
            cache.InvalidateCache();
            return Results.Ok(new { success = true, enabled });
        }
        catch (Exception ex)
        {
            Log.Warning("[library] Failed to update reader flag for {SafeFileName}: {Message}", safeFileName, ex.Message);
            return Results.Problem("Failed to update reader flag.");
        }
    }

    private static async Task<IResult> HandleWipeGenres(
        BookPersonalizationStore store,
        LibraryIndexCache cache)
    {
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return Results.Ok(new { success = true, updated = 0 });

        // Bulk admin wipe has to clear both layers: the user overrides in the store AND
        // the enrichment fallbacks in the sidecars — clearing only the store would let
        // the old sidecar genres shine straight back through the merge.
        store.ClearGenreFields();

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

        if (updatedCount > 0)
            cache.InvalidateCache();

        return Results.Ok(new { success = true, updated = updatedCount });
    }

    private static async Task<IResult> HandleGetSummary(
        [FromRoute] string fileName,
        [FromQuery] bool deep,
        IDescriptionFetcherService descriptionFetcher,
        LibraryIndexCache cache)
    {
        var safeFileName = SafeName(fileName);
        if (safeFileName == null)
            return ApiResponse.BadRequest("Invalid fileName.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

        if (!File.Exists(metaPath))
            return ApiResponse.NotFound("Metadata file not found.");

        try
        {
            var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
            var meta = await TryReadMetaAsync(metaPath);

            if (meta == null)
                return ApiResponse.BadRequest("Invalid metadata file.");

            // If we already have a description, return it
            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                return Results.Ok(new { summary = meta.Description, source = "cached" });
            }

            // Try to fetch from external sources using the centralized service
            var title = meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName);
            var author = meta.Authors?.FirstOrDefault();

            var result = await descriptionFetcher.FetchDescriptionAsync(title, author, useDeepModel: deep);
            var summary = result.Description;
            var source = result.Source;

            // A fetched description is enrichment data, not a user edit — it stays in the
            // sidecar. Safe to whole-file rewrite now that the model round-trips unknown fields.
            if (!string.IsNullOrWhiteSpace(summary))
            {
                try
                {
                    // Re-read to avoid race conditions
                    meta = await TryReadMetaAsync(metaPath);
                    if (meta != null)
                    {
                        var updated = meta with { Description = summary };
                        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
                        await File.WriteAllTextAsync(metaPath, updatedJson);
                        cache.InvalidateCache();
                        Log.Information("[library-summary] Saved summary for {SafeFileName} (source: {Source})", safeFileName, source);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[library-summary] Failed to save summary: {Message}", ex.Message);
                }
            }

            return Results.Ok(new { summary, source });
        }
        catch (Exception ex)
        {
            Log.Warning("[library-summary] Error for {SafeFileName}: {Message}", safeFileName, ex.Message);
            return Results.Problem("Failed to get summary.");
        }
    }
}
