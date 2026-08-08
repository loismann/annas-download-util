using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
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
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        //
        // Two groups off the same prefix. The "media" bucket is the looser limiter
        // for per-tile requests — a library grid renders hundreds at once and would
        // otherwise exhaust the whole "api" window on covers alone.
        var group = app.MapGroup("/api/media")
            .RequireAuthorization()
            .RequireRateLimiting("api");
        var mediaGroup = app.MapGroup("/api/media")
            .RequireAuthorization()
            .RequireRateLimiting("media");

        group.MapGet("/tv/downloaded", HandleGetDownloadedTv);

        group.MapGet("/tv/{seriesId:int}/episodes", HandleGetSeriesEpisodes);

        group.MapGet("/tv/watch", HandleWatchTv);

        // Native <video> stream for household members with personal Jellyfin
        // credentials configured (see JellyfinService.HasPersonalCredentials) —
        // only reachable after HandleWatchTv/HandleWatchMovie returns mode:"native"
        // for that person. Auth via ?access_token=, same reasoning as the
        // download routes below (a <video src> can't carry an Authorization header).
        mediaGroup.MapGet("/tv/stream", HandleStreamTv);

        // HLS fallback for files a plain <video src> can't decode natively (AVI
        // containers, AC3/DTS audio, etc — see JellyfinService.IsBrowserCompatible).
        // Only reachable after HandleWatchTv/HandleWatchMovie returns
        // playbackMode:"transcode" for that item.
        mediaGroup.MapGet("/tv/hls/master.m3u8", HandleTvHlsMaster);

        group.MapPost("/tv/progress", HandleSaveTvProgress);

        // Converts one embedded subtitle stream to WebVTT for a <track> element —
        // a browser can't parse embedded SRT/ASS/PGS out of a container itself.
        // Auth via ?access_token=, same reasoning as the stream routes (a <track
        // src> is a plain browser-issued GET, no custom headers possible).
        mediaGroup.MapGet("/tv/subtitles", HandleTvSubtitles);

        // Proxies the actual file down from Jellyfin (see JellyfinService.DownloadEpisodeAsync).
        // Rate-limited under "media" (large-file proxy), same convention as audiobook
        // stream/cover. Auth arrives via ?access_token= — see the OnMessageReceived
        // allowlist in ServiceConfiguration.cs — since this is a plain browser
        // navigation, not an XHR that could carry an Authorization header.
        mediaGroup.MapGet("/tv/download", HandleDownloadTv);

        group.MapGet("/movies/downloaded", HandleGetDownloadedMovies);

        group.MapGet("/movies/watch", HandleWatchMovie);

        mediaGroup.MapGet("/movies/stream", HandleStreamMovie);

        mediaGroup.MapGet("/movies/hls/master.m3u8", HandleMovieHlsMaster);

        // Shared HLS sub-resource proxy (the second-level playlist + each .ts
        // segment) for both movies and episodes alike — by this point the
        // caller only needs Jellyfin's own opaque itemId (already resolved once
        // by the master-playlist request above), not tmdbId/tvdbId again.
        mediaGroup.MapGet("/hls/{itemId}/{*subPath}", HandleHlsResource);

        group.MapPost("/movies/progress", HandleSaveMovieProgress);

        mediaGroup.MapGet("/movies/subtitles", HandleMovieSubtitles);

        mediaGroup.MapGet("/movies/download", HandleDownloadMovie);

        group.MapDelete("/tv/{seriesId:int}", HandleDeleteSeries);

        group.MapDelete("/tv/{seriesId:int}/season/{seasonNumber:int}", HandleDeleteSeason);

        group.MapDelete("/movies/{movieId:int}", HandleDeleteMovie);

        group.MapPatch("/tv/{seriesId:int}/metadata", HandleSetTvMetadata);

        group.MapPatch("/movies/{movieId:int}/metadata", HandleSetMovieMetadata);

        group.MapPost("/tv/{seriesId:int}/favorite", HandleSetTvFavorite);

        group.MapPost("/movies/{movieId:int}/favorite", HandleSetMovieFavorite);

        group.MapGet("/movies/{movieId:int}/releases", HandleSearchMovieReleases);

        group.MapPost("/movies/{movieId:int}/releases/grab", HandleGrabMovieRelease);

        group.MapGet("/tv/{seriesId:int}/season/{seasonNumber:int}/releases", HandleSearchSeasonReleases);

        group.MapPost("/tv/{seriesId:int}/season/{seasonNumber:int}/releases/grab", HandleGrabSeasonRelease);

        return app;
    }



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
                    ? ApiResponse.NotFound("Jellyfin hasn't matched this episode yet — it may still be scanning.")
                    : Results.Ok(new
                    {
                        mode = "native",
                        itemId = state.ItemId,
                        resumePositionSeconds = state.ResumePositionSeconds,
                        durationSeconds = state.DurationSeconds,
                        mediaSourceId = state.MediaSourceId,
                        audioTracks = state.AudioTracks,
                        subtitleTracks = state.SubtitleTracks,
                        playbackMode = state.PlaybackMode
                    });
            }

            var embedUrl = await jellyfin.GetTvEmbedUrlAsync(tvdbId, season, episode);
            return embedUrl is null
                ? ApiResponse.NotFound("Jellyfin hasn't matched this episode yet — it may still be scanning.")
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
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

        var saved = await jellyfin.SaveEpisodePositionAsync(owner, request.TvdbId, request.Season, request.Episode, request.PositionSeconds);
        return saved ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> HandleTvSubtitles(
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode,
        [FromQuery] string mediaSourceId, [FromQuery] int subtitleIndex, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

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

    /// <summary>Hides undownloaded Date Night pool movies from the regular library —
    /// the pool is a few hundred records deliberately added without files, and showing
    /// them here would bury the actual library under unwatchable tiles. A pool movie
    /// that *has* a file is left visible: at that point it's a real, playable movie
    /// like any other, which is also how a movie graduates out of the pool after a
    /// date night. See DOCS/features/DATE_NIGHT.md.</summary>
    private static async Task<IResult> HandleGetDownloadedMovies(IRadarrService radarr, IMediaMetadataService metadata)
    {
        try
        {
            var movies = await radarr.GetAllMoviesAsync();

            var poolTagId = await radarr.EnsureTagAsync(DateNight.PoolTag);
            var hidden = movies.OfType<JsonObject>()
                .Where(m => (bool?)m["hasFile"] != true
                            && (m["tags"] as JsonArray)?.Any(t => (int?)t == poolTagId) == true)
                .ToList();
            foreach (var movie in hidden)
                movies.Remove(movie);

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
                    ? ApiResponse.NotFound("Jellyfin hasn't matched this movie yet — it may still be scanning.")
                    : Results.Ok(new
                    {
                        mode = "native",
                        itemId = state.ItemId,
                        resumePositionSeconds = state.ResumePositionSeconds,
                        durationSeconds = state.DurationSeconds,
                        mediaSourceId = state.MediaSourceId,
                        audioTracks = state.AudioTracks,
                        subtitleTracks = state.SubtitleTracks,
                        playbackMode = state.PlaybackMode
                    });
            }

            var embedUrl = await jellyfin.GetMovieEmbedUrlAsync(tmdbId);
            return embedUrl is null
                ? ApiResponse.NotFound("Jellyfin hasn't matched this movie yet — it may still be scanning.")
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

    private static async Task<IResult> HandleMovieHlsMaster(HttpContext context, [FromQuery] int tmdbId, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

        try
        {
            var result = await jellyfin.GetMovieHlsMasterAsync(owner, tmdbId, context.RequestAborted);
            if (result is null)
                return ApiResponse.NotFound("Jellyfin hasn't matched this movie yet — it may still be scanning.");

            var accessToken = context.Request.Query["access_token"].ToString();
            return Results.Text(MediaLibraryRules.RewriteHlsPlaylist(result.PlaylistText, result.ItemId, accessToken), "application/vnd.apple.mpegurl");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin movie HLS master failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleTvHlsMaster(
        HttpContext context, [FromQuery] int tvdbId, [FromQuery] int season, [FromQuery] int episode, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

        try
        {
            var result = await jellyfin.GetEpisodeHlsMasterAsync(owner, tvdbId, season, episode, context.RequestAborted);
            if (result is null)
                return ApiResponse.NotFound("Jellyfin hasn't matched this episode yet — it may still be scanning.");

            var accessToken = context.Request.Query["access_token"].ToString();
            return Results.Text(MediaLibraryRules.RewriteHlsPlaylist(result.PlaylistText, result.ItemId, accessToken), "application/vnd.apple.mpegurl");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin episode HLS master failed: {Message}", ex.Message);
            return Results.Json(new { error = "Jellyfin is unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Proxies one HLS sub-resource — the second-level "main.m3u8" playlist,
    /// or a .ts media segment — for whichever itemId a master-playlist request
    /// already resolved. Playlists get the same URL rewrite as the master
    /// (RewriteHlsPlaylist); segments are raw bytes, relayed verbatim.</summary>
    private static async Task HandleHlsResource(HttpContext context, [FromRoute] string itemId, [FromRoute] string subPath, IJellyfinService jellyfin)
    {
        try
        {
            var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
            // Everything except our own access_token — Jellyfin doesn't know about
            // that one; the rest (its own api_key, VideoCodec, etc.) all came from
            // the playlist Jellyfin itself generated, so it rides back unchanged.
            var forwardedQuery = string.Join('&', context.Request.Query
                .Where(q => q.Key != "access_token")
                .SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? "")}")));

            var result = await jellyfin.ProxyHlsResourceAsync(itemId, subPath, forwardedQuery, rangeHeader, context.RequestAborted);
            if (result is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (result.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                string text;
                await using (result.Body)
                using (var reader = new StreamReader(result.Body))
                {
                    text = await reader.ReadToEndAsync();
                }
                var accessToken = context.Request.Query["access_token"].ToString();
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                await context.Response.WriteAsync(MediaLibraryRules.RewriteHlsPlaylist(text, itemId, accessToken), context.RequestAborted);
                return;
            }

            await RelayStreamAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Player paused/seeked/closed mid-stream — the browser aborts the
            // in-flight segment request every time. Routine, not an error.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Jellyfin HLS resource proxy failed: {Message}", ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    /// <summary>Rewrites every relative URL in a Jellyfin HLS playlist (the master
    /// playlist's reference to its own second-level playlist, and that playlist's
    /// references to each .ts segment) to route back through this app's own HLS
    /// proxy route instead of pointing at Jellyfin directly, which the browser
    /// can't reach. Carries the same ?access_token= the browser used to reach this
    /// endpoint, so every follow-up request still passes .RequireAuthorization()
    /// like every other media route.</summary>
    private static async Task<IResult> HandleSaveMovieProgress(
        [FromBody] SaveMovieProgressRequest request, HttpContext context, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

        var saved = await jellyfin.SaveMoviePositionAsync(owner, request.TmdbId, request.PositionSeconds);
        return saved ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> HandleMovieSubtitles(
        HttpContext context, [FromQuery] int tmdbId, [FromQuery] string mediaSourceId, [FromQuery] int subtitleIndex, IJellyfinService jellyfin)
    {
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner is null)
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

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
        // A movie/episode download is one long-lived, multi-GB write. Kestrel's
        // DEFAULT minimum response data rate (240 bytes/sec after a 5s grace
        // period — meant to guard against slow-loris-style attacks) stays
        // active for the whole response by default. On a real but imperfect
        // cellular connection, a transient stall anywhere in a multi-minute
        // transfer (a tower handoff, a signal dip) is enough to trip it —
        // Kestrel then aborts the connection server-side with no error
        // surfaced to the browser, so the download just silently never
        // appears. HLS streaming doesn't hit this: each segment is a short
        // request that finishes well inside the grace period regardless of
        // connection quality. Disabling the floor here (not globally in
        // Program.cs) keeps the protection for every other endpoint.
        var minRateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minRateFeature is not null)
            minRateFeature.MinDataRate = null;

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

    private static async Task<IResult> HandleDeleteSeries(
        [FromRoute] int seriesId, ISonarrService sonarr, IMediaMetadataService metadata)
    {
        try
        {
            await sonarr.DeleteSeriesAsync(seriesId);
            // The owners/genres/favorites record outlives the series unless it is
            // dropped here — the audiobook delete has always done this, the TV and
            // movie deletes never did, and that is where 73 orphaned keys came from.
            metadata.Delete("tv", seriesId.ToString());
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

    private static async Task<IResult> HandleDeleteMovie(
        [FromRoute] int movieId, IRadarrService radarr, IMediaMetadataService metadata)
    {
        try
        {
            await radarr.DeleteMovieAsync(movieId);
            metadata.Delete("movie", movieId.ToString());  // see HandleDeleteSeries
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
        var validated = MediaLibraryRules.ValidateMetadata(request);
        if (validated is null)
            return ApiResponse.BadRequest("owners may only contain Paul, Mom, Dad");

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
        var validated = MediaLibraryRules.ValidateMetadata(request);
        if (validated is null)
            return ApiResponse.BadRequest("owners may only contain Paul, Mom, Dad");

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
            return ApiResponse.BadRequest("Could not resolve the logged-in user.");

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
            // Interactive search can return an otherwise valid release with a null
            // movieId when Radarr cannot parse an obscure, alternate-language title
            // (for example a bilingual Nyaa release). That is precisely the case in
            // which Radarr says the movie must be "manually provided". The route is
            // already scoped to the authenticated user's chosen Radarr movie, so bind
            // that authoritative id before forwarding the raw release. Always
            // overwrite the client value rather than trusting a posted movieId.
            release["movieId"] = movieId;
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
            // Sonarr's interactive results have the same failure mode for obscure
            // or alternate-language series names. The route supplies the manual
            // association its release endpoint needs when parsing did not.
            release["seriesId"] = seriesId;
            await sonarr.GrabReleaseAsync(release);
            return Results.NoContent();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[MediaLibrary] Sonarr grab release failed: {Message}", ex.Message);
            return Results.Json(new { error = "Sonarr rejected the grab request" }, statusCode: StatusCodes.Status502BadGateway);
        }
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
