using System.Text.Json.Nodes;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for PATCH .../metadata — full replace of both fields.</summary>
public record SetMediaMetadataRequest(List<string>? Owners, List<string>? Genres);

/// <summary>
/// "What's actually downloaded, and how do I watch it" endpoints — distinct
/// from MediaRequestEndpoints (search/add) and from the unrelated, older
/// VideoLibraryBrowserEndpoints (which scans a flat folder of raw video
/// files, e.g. from the YouTube downloader — a different feature entirely).
/// Sonarr/Radarr remain the source of truth for download status; Jellyfin
/// is only consulted at watch-time, to resolve a playable embed URL.
/// </summary>
public static class MediaLibraryEndpoints
{
    public static WebApplication MapMediaLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/media/tv/downloaded", HandleGetDownloadedTv)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/tv/{seriesId:int}/episodes", HandleGetSeriesEpisodes)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/tv/watch", HandleWatchTv)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/downloaded", HandleGetDownloadedMovies)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/watch", HandleWatchMovie)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapDelete("/api/media/tv/{seriesId:int}", HandleDeleteSeries)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapDelete("/api/media/tv/{seriesId:int}/season/{seasonNumber:int}", HandleDeleteSeason)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapDelete("/api/media/movies/{movieId:int}", HandleDeleteMovie)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPatch("/api/media/tv/{seriesId:int}/metadata", HandleSetTvMetadata)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPatch("/api/media/movies/{movieId:int}/metadata", HandleSetMovieMetadata)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static readonly HashSet<string> ValidOwners = new(StringComparer.OrdinalIgnoreCase) { "Paul", "Mom", "Dad" };

    private static async Task<IResult> HandleGetDownloadedTv(ISonarrService sonarr, IMediaMetadataService metadata)
    {
        try
        {
            var series = await sonarr.GetAllSeriesAsync();
            ApplyMetadata(series, "tv", metadata);
            return Results.Ok(series);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr library fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGetSeriesEpisodes([FromRoute] int seriesId, ISonarrService sonarr)
    {
        try
        {
            var episodes = await sonarr.GetEpisodesAsync(seriesId);
            return Results.Ok(episodes);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr episodes fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleWatchTv(
        [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode, IJellyfinService jellyfin)
    {
        try
        {
            var embedUrl = await jellyfin.GetTvEmbedUrlAsync(tvdbId, season, episode);
            return embedUrl is null
                ? Results.NotFound(new { error = "Jellyfin hasn't matched this episode yet — it may still be scanning." })
                : Results.Ok(new { embedUrl });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin lookup failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGetDownloadedMovies(IRadarrService radarr, IMediaMetadataService metadata)
    {
        try
        {
            var movies = await radarr.GetAllMoviesAsync();
            ApplyMetadata(movies, "movie", metadata);
            return Results.Ok(movies);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Radarr library fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleWatchMovie([FromQuery] int tmdbId, IJellyfinService jellyfin)
    {
        try
        {
            var embedUrl = await jellyfin.GetMovieEmbedUrlAsync(tmdbId);
            return embedUrl is null
                ? Results.NotFound(new { error = "Jellyfin hasn't matched this movie yet — it may still be scanning." })
                : Results.Ok(new { embedUrl });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin lookup failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleDeleteSeries([FromRoute] int seriesId, ISonarrService sonarr)
    {
        try
        {
            await sonarr.DeleteSeriesAsync(seriesId);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr delete series failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the delete request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleDeleteSeason(
        [FromRoute] int seriesId, [FromRoute] int seasonNumber, ISonarrService sonarr)
    {
        try
        {
            await sonarr.DeleteSeasonAsync(seriesId, seasonNumber);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr delete season failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the delete request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleDeleteMovie([FromRoute] int movieId, IRadarrService radarr)
    {
        try
        {
            await radarr.DeleteMovieAsync(movieId);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Radarr delete movie failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr rejected the delete request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult HandleSetTvMetadata([FromRoute] int seriesId, [FromBody] SetMediaMetadataRequest request, IMediaMetadataService metadata)
    {
        var validated = ValidateMetadata(request);
        if (validated is null)
            return Results.BadRequest(new { error = "owners may only contain Paul, Mom, Dad" });

        metadata.Set("tv", seriesId, validated);
        return Results.NoContent();
    }

    private static IResult HandleSetMovieMetadata([FromRoute] int movieId, [FromBody] SetMediaMetadataRequest request, IMediaMetadataService metadata)
    {
        var validated = ValidateMetadata(request);
        if (validated is null)
            return Results.BadRequest(new { error = "owners may only contain Paul, Mom, Dad" });

        metadata.Set("movie", movieId, validated);
        return Results.NoContent();
    }

    private static MediaItemMetadata? ValidateMetadata(SetMediaMetadataRequest request)
    {
        var owners = (request.Owners ?? new List<string>())
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (owners.Any(o => !ValidOwners.Contains(o)))
            return null;

        var genres = (request.Genres ?? new List<string>())
            .Select(g => g.Trim())
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MediaItemMetadata(owners, genres);
    }

    /// <summary>Merges each item's recorded owners/genres (if any) into its raw
    /// Sonarr/Radarr JSON as "owners"/"customGenres" fields, matched by that
    /// item's own "id". Named "customGenres" (not "genres") so it doesn't
    /// collide with Sonarr/Radarr's own read-only genre field on the same object.</summary>
    private static void ApplyMetadata(JsonArray items, string type, IMediaMetadataService metadataService)
    {
        var all = metadataService.GetAll();
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["id"] is null) continue;
            var meta = all.GetValueOrDefault($"{type}:{obj["id"]!.GetValue<int>()}");
            obj["owners"] = new JsonArray((meta?.Owners ?? new List<string>()).Select(o => (JsonNode)o).ToArray());
            obj["customGenres"] = new JsonArray((meta?.Genres ?? new List<string>()).Select(g => (JsonNode)g).ToArray());
        }
    }
}
