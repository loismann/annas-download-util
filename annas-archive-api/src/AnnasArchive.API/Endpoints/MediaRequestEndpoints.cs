using System.Text.Json.Nodes;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for POST /api/media/tv/add — wraps the raw Sonarr lookup object
/// alongside an optional season-picker selection (null/empty = monitor everything).</summary>
public record AddSeriesRequest(JsonObject Series, List<int>? SelectedSeasons);

/// <summary>Body for POST /api/media/tv/update-seasons — for a series that's already
/// added, adds more monitored seasons instead of re-adding it from scratch.</summary>
public record UpdateSeasonsRequest(int SeriesId, List<int> SelectedSeasons);

/// <summary>
/// TV/movie search-and-acquire endpoints — a thin proxy in front of Sonarr
/// and Radarr's own REST APIs, so the frontend gets one consistent app
/// (matching the book-search flow) instead of linking out to their separate
/// dashboards.
/// </summary>
public static class MediaRequestEndpoints
{
    public static WebApplication MapMediaRequestEndpoints(this WebApplication app)
    {
        app.MapGet("/api/media/tv/search", HandleTvSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/tv/add", HandleTvAdd)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/tv/library", HandleGetTvLibrary)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/tv/update-seasons", HandleUpdateTvSeasons)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/search", HandleMovieSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/movies/add", HandleMovieAdd)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/queue", HandleGetQueue)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleTvSearch([FromQuery] string? term, ISonarrService sonarr)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        try
        {
            var results = await sonarr.LookupSeriesAsync(term);
            return Results.Ok(results);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleTvAdd(
        [FromBody] AddSeriesRequest request, ISonarrService sonarr, IMediaMetadataService metadata, HttpContext context)
    {
        try
        {
            var added = await sonarr.AddSeriesAsync(request.Series, request.SelectedSeasons);
            if (added["id"] is not null)
            {
                var owner = LibraryHelpers.ResolveUserDisplayName(context);
                if (owner is not null)
                    metadata.AddOwner("tv", added["id"]!.GetValue<int>(), owner);
            }
            return Results.Ok(added);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr add failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleGetTvLibrary(ISonarrService sonarr)
    {
        try
        {
            var series = await sonarr.GetAllSeriesAsync();
            return Results.Ok(series);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr library fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleUpdateTvSeasons([FromBody] UpdateSeasonsRequest request, ISonarrService sonarr)
    {
        try
        {
            var updated = await sonarr.UpdateSeriesSeasonsAsync(request.SeriesId, request.SelectedSeasons);
            return Results.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Sonarr update-seasons failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleMovieSearch([FromQuery] string? term, IRadarrService radarr)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Results.BadRequest(new { error = "term is required." });

        try
        {
            var results = await radarr.LookupMoviesAsync(term);
            return Results.Ok(results);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Radarr search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleMovieAdd(
        [FromBody] JsonObject movie, IRadarrService radarr, IMediaMetadataService metadata, HttpContext context)
    {
        try
        {
            var added = await radarr.AddMovieAsync(movie);
            if (added["id"] is not null)
            {
                var owner = LibraryHelpers.ResolveUserDisplayName(context);
                if (owner is not null)
                    metadata.AddOwner("movie", added["id"]!.GetValue<int>(), owner);
            }
            return Results.Ok(added);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Radarr add failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr rejected the request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleGetQueue(ISonarrService sonarr, IRadarrService radarr)
    {
        try
        {
            var tvQueue = await sonarr.GetQueueAsync();
            var movieQueue = await radarr.GetQueueAsync();
            return Results.Ok(new { tv = tvQueue, movies = movieQueue });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaRequest] Queue fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Queue temporarily unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
