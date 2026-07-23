using System.Text.Json.Nodes;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for PATCH .../owner — null/blank clears the assignment.</summary>
public record SetOwnerRequest(string? Owner);

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

        app.MapPatch("/api/media/tv/{seriesId:int}/owner", HandleSetTvOwner)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPatch("/api/media/movies/{movieId:int}/owner", HandleSetMovieOwner)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static readonly HashSet<string> ValidOwners = new(StringComparer.OrdinalIgnoreCase) { "Paul", "Mom", "Dad" };

    private static async Task<IResult> HandleGetDownloadedTv(ISonarrService sonarr, IMediaOwnershipService ownership)
    {
        try
        {
            var series = await sonarr.GetAllSeriesAsync();
            ApplyOwners(series, "tv", ownership);
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

    private static async Task<IResult> HandleGetDownloadedMovies(IRadarrService radarr, IMediaOwnershipService ownership)
    {
        try
        {
            var movies = await radarr.GetAllMoviesAsync();
            ApplyOwners(movies, "movie", ownership);
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

    private static IResult HandleSetTvOwner([FromRoute] int seriesId, [FromBody] SetOwnerRequest request, IMediaOwnershipService ownership)
    {
        if (!string.IsNullOrWhiteSpace(request.Owner) && !ValidOwners.Contains(request.Owner))
            return Results.BadRequest(new { error = "owner must be one of Paul, Mom, Dad, or null" });

        ownership.SetOwner("tv", seriesId, request.Owner);
        return Results.NoContent();
    }

    private static IResult HandleSetMovieOwner([FromRoute] int movieId, [FromBody] SetOwnerRequest request, IMediaOwnershipService ownership)
    {
        if (!string.IsNullOrWhiteSpace(request.Owner) && !ValidOwners.Contains(request.Owner))
            return Results.BadRequest(new { error = "owner must be one of Paul, Mom, Dad, or null" });

        ownership.SetOwner("movie", movieId, request.Owner);
        return Results.NoContent();
    }

    /// <summary>Merges each item's recorded owner (if any) into its raw Sonarr/Radarr
    /// JSON as an "owner" field, matched by that item's own "id".</summary>
    private static void ApplyOwners(JsonArray items, string type, IMediaOwnershipService ownership)
    {
        var owners = ownership.GetAllOwners();
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["id"] is null) continue;
            var owner = owners.GetValueOrDefault($"{type}:{obj["id"]!.GetValue<int>()}");
            obj["owner"] = owner;
        }
    }
}
