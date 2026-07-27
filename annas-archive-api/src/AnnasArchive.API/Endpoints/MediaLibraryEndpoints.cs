using System.Text.Json.Nodes;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for PATCH .../metadata — full replace of Owners/Genres. Title is
/// audiobooks-only (see AudiobookLibraryEndpoints.ValidateMetadata) — TV/movie
/// titles come from Sonarr/Radarr's own matching and are never user-editable,
/// so MediaLibraryEndpoints.ValidateMetadata below just never reads it.</summary>
public record SetMediaMetadataRequest(List<string>? Owners, List<string>? Genres, string? Title = null);

/// <summary>Body for POST .../favorite — the acting owner is resolved server-side from the
/// authenticated session, never taken from the client.</summary>
public record SetMediaFavoriteRequest(bool Favorited);

/// <summary>Body for POST .../movies/progress — tmdbId (not Radarr's own movieId) to match
/// the id space watch/download/stream already use for this same item.</summary>
public record SaveMovieProgressRequest(int TmdbId, double PositionSeconds);

/// <summary>Body for POST .../tv/progress — episode identity has no single route-friendly
/// id the way a movie does, so it rides in the body alongside the position.</summary>
public record SaveTvProgressRequest(int TvdbId, int Season, int Episode, double PositionSeconds);

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

        // Native <video> stream for household members with personal Jellyfin
        // credentials configured (see JellyfinService.HasPersonalCredentials) —
        // only reachable after HandleWatchTv/HandleWatchMovie returns mode:"native"
        // for that person. Auth via ?access_token=, same reasoning as the
        // download routes below (a <video src> can't carry an Authorization header).
        app.MapGet("/api/media/tv/stream", HandleStreamTv)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        app.MapPost("/api/media/tv/progress", HandleSaveTvProgress)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Converts one embedded subtitle stream to WebVTT for a <track> element —
        // a browser can't parse embedded SRT/ASS/PGS out of a container itself.
        // Auth via ?access_token=, same reasoning as the stream routes (a <track
        // src> is a plain browser-issued GET, no custom headers possible).
        app.MapGet("/api/media/tv/subtitles", HandleTvSubtitles)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        // Proxies the actual file down from Jellyfin (see JellyfinService.DownloadEpisodeAsync).
        // Rate-limited under "media" (large-file proxy), same convention as audiobook
        // stream/cover. Auth arrives via ?access_token= — see the OnMessageReceived
        // allowlist in ServiceConfiguration.cs — since this is a plain browser
        // navigation, not an XHR that could carry an Authorization header.
        app.MapGet("/api/media/tv/download", HandleDownloadTv)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        app.MapGet("/api/media/movies/downloaded", HandleGetDownloadedMovies)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/watch", HandleWatchMovie)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/stream", HandleStreamMovie)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        app.MapPost("/api/media/movies/progress", HandleSaveMovieProgress)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/subtitles", HandleMovieSubtitles)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        app.MapGet("/api/media/movies/download", HandleDownloadMovie)
            .RequireAuthorization()
            .RequireRateLimiting("media");

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

        app.MapPost("/api/media/tv/{seriesId:int}/favorite", HandleSetTvFavorite)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/movies/{movieId:int}/favorite", HandleSetMovieFavorite)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/movies/{movieId:int}/releases", HandleSearchMovieReleases)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/movies/{movieId:int}/releases/grab", HandleGrabMovieRelease)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/media/tv/{seriesId:int}/season/{seasonNumber:int}/releases", HandleSearchSeasonReleases)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/media/tv/{seriesId:int}/season/{seasonNumber:int}/releases/grab", HandleGrabSeasonRelease)
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
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        try
        {
            if (owner is not null && jellyfin.HasPersonalCredentials(owner))
            {
                var state = await jellyfin.GetEpisodePlaybackStateAsync(owner, tvdbId, season, episode);
                return state is null
                    ? Results.NotFound(new { error = "Jellyfin hasn't matched this episode yet — it may still be scanning." })
                    : Results.Ok(new
                    {
                        mode = "native",
                        itemId = state.ItemId,
                        resumePositionSeconds = state.ResumePositionSeconds,
                        durationSeconds = state.DurationSeconds,
                        mediaSourceId = state.MediaSourceId,
                        audioTracks = state.AudioTracks,
                        subtitleTracks = state.SubtitleTracks
                    });
            }

            var embedUrl = await jellyfin.GetTvEmbedUrlAsync(tvdbId, season, episode);
            return embedUrl is null
                ? Results.NotFound(new { error = "Jellyfin hasn't matched this episode yet — it may still be scanning." })
                : Results.Ok(new { mode = "embed", embedUrl });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin lookup failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleStreamTv(
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        try
        {
            var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
            var result = await jellyfin.StreamEpisodeAsync(owner, tvdbId, season, episode, rangeHeader, context.RequestAborted);
            if (result is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            await RelayStreamAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Player paused/seeked/closed mid-stream — the browser aborts the
            // in-flight range request every time. Routine, not an error.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin episode stream failed: {Message}", ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    private static async Task<IResult> HandleSaveTvProgress(
        [FromBody] SaveTvProgressRequest request, HttpContext context, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        var saved = await jellyfin.SaveEpisodePositionAsync(owner, request.TvdbId, request.Season, request.Episode, request.PositionSeconds);
        return saved ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> HandleTvSubtitles(
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode,
        [FromQuery] string mediaSourceId, [FromQuery] int subtitleIndex, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        var vtt = await jellyfin.GetEpisodeSubtitleVttAsync(owner, tvdbId, season, episode, mediaSourceId, subtitleIndex);
        return vtt is null ? Results.NotFound() : Results.Text(vtt, "text/vtt");
    }

    private static async Task HandleDownloadTv(
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode, IJellyfinService jellyfin)
    {
        try
        {
            var result = await jellyfin.DownloadEpisodeAsync(tvdbId, season, episode, context.RequestAborted);
            if (result is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            await RelayDownloadAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Browser canceled the download (closed the tab, hit stop) — routine.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin episode download failed: {Message}", ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
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

    private static async Task<IResult> HandleWatchMovie(HttpContext context, [FromQuery] int tmdbId, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        try
        {
            if (owner is not null && jellyfin.HasPersonalCredentials(owner))
            {
                var state = await jellyfin.GetMoviePlaybackStateAsync(owner, tmdbId);
                return state is null
                    ? Results.NotFound(new { error = "Jellyfin hasn't matched this movie yet — it may still be scanning." })
                    : Results.Ok(new
                    {
                        mode = "native",
                        itemId = state.ItemId,
                        resumePositionSeconds = state.ResumePositionSeconds,
                        durationSeconds = state.DurationSeconds,
                        mediaSourceId = state.MediaSourceId,
                        audioTracks = state.AudioTracks,
                        subtitleTracks = state.SubtitleTracks
                    });
            }

            var embedUrl = await jellyfin.GetMovieEmbedUrlAsync(tmdbId);
            return embedUrl is null
                ? Results.NotFound(new { error = "Jellyfin hasn't matched this movie yet — it may still be scanning." })
                : Results.Ok(new { mode = "embed", embedUrl });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin lookup failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleStreamMovie(HttpContext context, [FromQuery] int tmdbId, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        try
        {
            var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
            var result = await jellyfin.StreamMovieAsync(owner, tmdbId, rangeHeader, context.RequestAborted);
            if (result is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            await RelayStreamAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Player paused/seeked/closed mid-stream — the browser aborts the
            // in-flight range request every time. Routine, not an error.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin movie stream failed: {Message}", ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    private static async Task<IResult> HandleSaveMovieProgress(
        [FromBody] SaveMovieProgressRequest request, HttpContext context, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        var saved = await jellyfin.SaveMoviePositionAsync(owner, request.TmdbId, request.PositionSeconds);
        return saved ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> HandleMovieSubtitles(
        HttpContext context, [FromQuery] int tmdbId, [FromQuery] string mediaSourceId, [FromQuery] int subtitleIndex, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        var vtt = await jellyfin.GetMovieSubtitleVttAsync(owner, tmdbId, mediaSourceId, subtitleIndex);
        return vtt is null ? Results.NotFound() : Results.Text(vtt, "text/vtt");
    }

    /// <summary>Relays a proxied per-user Jellyfin stream response (status, Content-Type,
    /// Content-Range/Content-Length when present, and body) straight through to the
    /// browser — same partial-content contract as the audiobook stream proxy.</summary>
    private static async Task RelayStreamAsync(HttpContext context, JellyfinStreamResult result)
    {
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = result.ContentType;
        context.Response.Headers.AcceptRanges = "bytes";
        if (result.ContentRange is not null)
            context.Response.Headers.ContentRange = result.ContentRange;
        if (result.ContentLength is not null)
            context.Response.ContentLength = result.ContentLength;

        await using (result.Body)
        {
            await result.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static async Task HandleDownloadMovie(HttpContext context, [FromQuery] int tmdbId, IJellyfinService jellyfin)
    {
        try
        {
            var result = await jellyfin.DownloadMovieAsync(tmdbId, context.RequestAborted);
            if (result is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            await RelayDownloadAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Browser canceled the download (closed the tab, hit stop) — routine.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin movie download failed: {Message}", ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    /// <summary>Relays a Jellyfin file download straight through to the browser as
    /// a real download (Content-Disposition: attachment), streaming rather than
    /// buffering — movie files can be several GB.</summary>
    private static async Task RelayDownloadAsync(HttpContext context, JellyfinDownloadResult result)
    {
        context.Response.ContentType = result.ContentType;
        if (result.ContentLength is not null)
            context.Response.ContentLength = result.ContentLength;

        var fileName = string.IsNullOrWhiteSpace(result.FileName) ? "download" : result.FileName;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        await using (result.Body)
        {
            await result.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
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

        try
        {
            metadata.Set("tv", seriesId.ToString(), validated);
            Log.Information("[MediaLibrary] Set tv:{SeriesId} metadata: owners={Owners}, genres={Genres}",
                seriesId, string.Join(",", validated.Owners), string.Join(",", validated.Genres));
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaLibrary] Failed to save tv:{SeriesId} metadata: {Message}", seriesId, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult HandleSetMovieMetadata([FromRoute] int movieId, [FromBody] SetMediaMetadataRequest request, IMediaMetadataService metadata)
    {
        var validated = ValidateMetadata(request);
        if (validated is null)
            return Results.BadRequest(new { error = "owners may only contain Paul, Mom, Dad" });

        try
        {
            metadata.Set("movie", movieId.ToString(), validated);
            Log.Information("[MediaLibrary] Set movie:{MovieId} metadata: owners={Owners}, genres={Genres}",
                movieId, string.Join(",", validated.Owners), string.Join(",", validated.Genres));
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaLibrary] Failed to save movie:{MovieId} metadata: {Message}", movieId, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult HandleSetTvFavorite(
        [FromRoute] int seriesId, [FromBody] SetMediaFavoriteRequest request, HttpContext context, IMediaMetadataService metadata)
        => HandleSetFavorite("tv", seriesId, request, context, metadata);

    private static IResult HandleSetMovieFavorite(
        [FromRoute] int movieId, [FromBody] SetMediaFavoriteRequest request, HttpContext context, IMediaMetadataService metadata)
        => HandleSetFavorite("movie", movieId, request, context, metadata);

    private static IResult HandleSetFavorite(
        string type, int id, SetMediaFavoriteRequest request, HttpContext context, IMediaMetadataService metadata)
    {
        // Who's favoriting is resolved from the authenticated session, not a client-supplied
        // value — same reasoning as the book library's favorite endpoint.
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner == null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        try
        {
            metadata.SetFavorite(type, id.ToString(), owner, request.Favorited);
            var updated = metadata.Get(type, id.ToString());
            return Results.Ok(new { success = true, favorites = updated?.Favorites ?? new List<string>() });
        }
        catch (Exception ex)
        {
            Log.Warning("[MediaLibrary] Failed to save {Type}:{Id} favorite: {Message}", type, id, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleSearchMovieReleases([FromRoute] int movieId, IRadarrService radarr)
    {
        try
        {
            var releases = await radarr.SearchReleasesAsync(movieId);
            return Results.Ok(releases);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Radarr release search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGrabMovieRelease(
        [FromRoute] int movieId, [FromBody] JsonObject release, IRadarrService radarr)
    {
        try
        {
            await radarr.GrabReleaseAsync(release);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Radarr grab release failed: {Message}", ex.Message);
            return Results.Json(new { error = "Radarr rejected the grab request" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleSearchSeasonReleases(
        [FromRoute] int seriesId, [FromRoute] int seasonNumber, ISonarrService sonarr)
    {
        try
        {
            var releases = await sonarr.SearchSeasonReleasesAsync(seriesId, seasonNumber);
            return Results.Ok(releases);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr release search failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleGrabSeasonRelease(
        [FromRoute] int seriesId, [FromRoute] int seasonNumber, [FromBody] JsonObject release, ISonarrService sonarr)
    {
        try
        {
            await sonarr.GrabReleaseAsync(release);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr grab release failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the grab request" }, statusCode: StatusCodes.Status502BadGateway);
        }
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
            var meta = all.GetValueOrDefault($"{type}:{obj["id"]!.GetValue<int>().ToString()}");
            obj["owners"] = new JsonArray((meta?.Owners ?? new List<string>()).Select(o => (JsonNode)o).ToArray());
            obj["customGenres"] = new JsonArray((meta?.Genres ?? new List<string>()).Select(g => (JsonNode)g).ToArray());
            obj["favorites"] = new JsonArray((meta?.Favorites ?? new List<string>()).Select(f => (JsonNode)f).ToArray());
        }
    }
}
