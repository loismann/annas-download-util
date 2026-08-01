using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;
using AnnasArchive.API.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Endpoints;

public static class SpotifyEndpoints
{
    public static WebApplication MapSpotifyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/spotify")
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("api");

        group.MapGet("/connection", HandleGetConnection);
        group.MapPost("/connection/authorize", HandleAuthorize);
        group.MapDelete("/connection", HandleDisconnect);
        group.MapGet("/search", HandleSearch);
        group.MapGet("/playlists", HandleGetPlaylists);
        group.MapGet("/playlists/{playlistId}", HandleGetPlaylist);
        group.MapGet("/playlists/{playlistId}/items", HandleGetPlaylistItems);
        group.MapPost("/command", HandleCommand);

        app.MapGet("/api/spotify/oauth/callback", HandleOAuthCallback)
            .AllowAnonymous()
            .RequireRateLimiting("login");

        return app;
    }

    private static IResult HandleGetConnection(
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser) =>
        Results.Ok(authorization.GetStatus(currentUser.GetRequiredOwnerKey()));

    private static IResult HandleAuthorize(
        SpotifyAuthorizeRequest? request,
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser)
    {
        try
        {
            var uri = authorization.CreateAuthorizationUri(
                currentUser.GetRequiredOwnerKey(),
                request?.ForceDialog ?? false);
            return Results.Ok(new SpotifyAuthorizeResponse(uri.AbsoluteUri));
        }
        catch (Exception ex)
        {
            return MapFailure(ex);
        }
    }

    private static IResult HandleDisconnect(
        ISpotifyAuthorizationService authorization,
        ISpotifyCurrentUser currentUser)
    {
        authorization.Disconnect(currentUser.GetRequiredOwnerKey());
        return Results.NoContent();
    }

    private static async Task<IResult> HandleOAuthCallback(
        string? state,
        string? code,
        string? error,
        ISpotifyAuthorizationService authorization,
        IOptions<SpotifyConfiguration> config,
        CancellationToken token)
    {
        string result;
        try
        {
            var completion = await authorization.CompleteAuthorizationAsync(state, code, error, token);
            result = completion.Succeeded ? "connected" : completion.Error ?? "authorization_failed";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] OAuth callback failed");
            result = ex is SpotifyConnectionException connectionException
                ? connectionException.State
                : "authorization_failed";
        }

        return Results.Redirect(BuildCallbackRedirect(result, config.Value.FrontendBaseUrl));
    }

    /// <summary>
    /// Where the browser lands after Spotify's callback. The <c>?spotify=</c> result
    /// must sit in a real query string, which is only true because the app serves
    /// path-based URLs. Under the previous hash routing this same value landed
    /// before the <c>#</c>, where Angular's router could not see it — a successful
    /// connection and a failed one both looked like silently landing on the wrong
    /// page. Reintroducing <c>#/</c> here would restore that bug.
    /// </summary>
    public static string BuildCallbackRedirect(string result, string? frontendBaseUrl)
    {
        var path = $"/spotifinator?spotify={Uri.EscapeDataString(result)}";
        var trimmedBase = frontendBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmedBase) ? path : trimmedBase + path;
    }

    private static async Task<IResult> HandleSearch(
        string q,
        int? limit,
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new { error = "Search query 'q' is required." });

        try
        {
            var results = await spotifyService.SearchTracksAsync(q, limit ?? 10, token);
            return Results.Ok(results);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Search failed for query: {Query}", q);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleGetPlaylists(
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var playlists = await spotifyService.GetUserPlaylistsAsync(token);
            return Results.Ok(playlists);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Failed to get playlists");
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleGetPlaylist(
        string playlistId,
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var playlist = await spotifyService.GetPlaylistAsync(playlistId, token);
            return playlist == null
                ? Results.NotFound(new { error = "That playlist could not be found." })
                : Results.Ok(playlist);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Failed to read playlist {PlaylistId}", playlistId);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleGetPlaylistItems(
        string playlistId,
        int? offset,
        int? limit,
        ISpotifyService spotifyService,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var page = await spotifyService.GetPlaylistItemsAsync(
                playlistId, offset ?? 0, limit ?? 50, token);
            return Results.Ok(page);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Spotify] Failed to read items of playlist {PlaylistId}", playlistId);
            return MapFailure(ex, context);
        }
    }

    private static async Task<IResult> HandleCommand(
        [FromBody] SpotifyConversationRequest request,
        [FromServices] ISpotifyConversationService conversation,
        HttpContext context,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
            return Results.BadRequest(new { error = "Message is required." });

        try
        {
            return Results.Ok(await conversation.HandleAsync(request, token));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Spotify] Command failed");
            return MapFailure(ex, context);
        }
    }

    private static IResult MapFailure(Exception exception, HttpContext? context = null)
    {
        var (statusCode, state, reason, retryAfter, message) = exception switch
        {
            SpotifyApiException apiException => (
                (int)apiException.SpotifyStatusCode,
                apiException.IsQuotaExceeded ? "QuotaExceeded" :
                    apiException.SpotifyStatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "RateLimited"
                        : "Connected",
                apiException.Reason,
                apiException.RetryAfter,
                apiException.SpotifyMessage ?? "Spotify rejected the request."),
            SpotifyConnectionException connectionException => (
                (int)connectionException.StatusCode,
                connectionException.State,
                (string?)null,
                connectionException.RetryAfter,
                connectionException.Message),
            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "Disconnected",
                (string?)null,
                (TimeSpan?)null,
                "A logged-in administrator is required for Spotify."),
            _ => (
                StatusCodes.Status502BadGateway,
                "SpotifyUnavailable",
                (string?)null,
                (TimeSpan?)null,
                "The Spotify operation could not be completed.")
        };

        var retryAfterSeconds = retryAfter is { } delay
            ? Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds))
            : (int?)null;
        if (context != null && retryAfterSeconds.HasValue)
            context.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString();

        return Results.Json(
            new SpotifyConnectionErrorDto(message, state, reason, retryAfterSeconds),
            statusCode: statusCode);
    }
}
