using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Body for POST .../progress — resume-position save.</summary>
public record SetAudiobookProgressRequest(double PositionSeconds);

/// <summary>Body for POST .../cover — an http(s) URL to download and use as
/// this audiobook's cover override (candidate picked from search, or pasted
/// manually).</summary>
public record SetAudiobookCoverRequest(string CoverUrl);

/// <summary>
/// Audiobook library endpoints — same "query the specialized tool's API,
/// merge in our own owners/customGenres/favorites" shape as
/// MediaLibraryEndpoints (TV/movies via Sonarr/Radarr), but kept as its own
/// file rather than folded in: the route prefix differs (/api/audiobooks,
/// not /api/media), and Audiobookshelf's item ids are string UUIDs rather
/// than Sonarr/Radarr's integer ids, so the owners/genres/favorites merge
/// step needs its own small variant rather than sharing MediaLibraryEndpoints'
/// int-keyed ApplyMetadata.
///
/// Uses the ebook library's plain RequireAuthorization() convention (not
/// AdminOnly) — audiobooks are household-visible/editable like books, not
/// admin-gated like the video library.
/// </summary>
public static class AudiobookLibraryEndpoints
{
    private static readonly HashSet<string> ValidOwners = new(StringComparer.OrdinalIgnoreCase) { "Paul", "Mom", "Dad" };

    /// <summary>Caps concurrent cover fetches against Audiobookshelf. Covers are
    /// lazy-loaded client-side, but a fast scroll can still burst dozens of
    /// requests at once and an ABS instance on NAS hardware folds under that —
    /// excess requests just wait their turn here (covers are small, so slots
    /// turn over in milliseconds). Audio streams are not gated: there's only
    /// ever a player or two open.</summary>
    private static readonly SemaphoreSlim CoverFetchGate = new(8);

    public static WebApplication MapAudiobookLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audiobooks", HandleGetCatalog)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/audiobooks/{id}", HandleGetItem)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPatch("/api/audiobooks/{id}/metadata", HandleSetMetadata)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobooks/{id}/favorite", HandleSetFavorite)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobooks/{id}/progress", HandleSetProgress)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Cover override — downloads and stores a user-picked cover locally (see
        // StoragePaths.AudiobookCoverOverrideRoot); never writes to Audiobookshelf's
        // own storage. HandleGetCover below serves it in place of the ABS proxy once set.
        app.MapPost("/api/audiobooks/{id}/cover", HandleSetCover)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Stream/cover are also RequireAuthorization() — auth for these two
        // routes specifically comes via the ?access_token= query-string JWT
        // handler registered in ServiceConfiguration.cs, since native
        // <audio>/<img> elements can't carry an Authorization header.
        //
        // Rate-limited under "media" (not "api"): the catalog tile grid renders every
        // filtered item's cover <img> at once with no pagination, so a single page load can
        // fire hundreds of these — sharing the 60/min "api" budget meant those covers alone
        // exhausted it, causing legitimate calls (like opening the player) to get rejected.
        app.MapGet("/api/audiobooks/{id}/cover", HandleGetCover)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        app.MapGet("/api/audiobooks/{id}/stream/{ino}", HandleStream)
            .RequireAuthorization()
            .RequireRateLimiting("media");

        return app;
    }

    private static async Task<IResult> HandleGetCatalog(IAudiobookshelfService abs, IMediaMetadataService metadata)
    {
        try
        {
            var items = await abs.GetLibraryItemsAsync();
            ApplyMetadata(items, metadata);
            return Results.Json(items);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            Log.Warning("[Audiobooks] Audiobookshelf catalog fetch failed: {Message}", ex.Message);
            return Results.Json(new { error = "Audiobookshelf is unavailable" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleGetItem([FromRoute] string id, IAudiobookshelfService abs, IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null) return Results.BadRequest(new { error = "Invalid id." });

        try
        {
            var item = await abs.GetItemAsync(safeId);
            if (item is null) return Results.NotFound(new { error = "Audiobook not found." });

            var items = new JsonArray(item.DeepClone());
            ApplyMetadata(items, metadata);
            return Results.Json(items[0]);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            Log.Warning("[Audiobooks] Audiobookshelf item fetch failed for {Id}: {Message}", safeId, ex.Message);
            return Results.Json(new { error = "Audiobookshelf is unavailable" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult HandleSetMetadata([FromRoute] string id, [FromBody] SetMediaMetadataRequest request, IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null) return Results.BadRequest(new { error = "Invalid id." });

        var validated = ValidateMetadata(request);
        if (validated is null)
            return Results.BadRequest(new { error = "owners may only contain Paul, Mom, Dad" });

        try
        {
            metadata.Set("audiobook", safeId, validated);
            Log.Information("[Audiobooks] Set audiobook:{Id} metadata: owners={Owners}, genres={Genres}",
                safeId, string.Join(",", validated.Owners), string.Join(",", validated.Genres));
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.Warning("[Audiobooks] Failed to save audiobook:{Id} metadata: {Message}", safeId, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult HandleSetFavorite(
        [FromRoute] string id, [FromBody] SetMediaFavoriteRequest request, HttpContext context, IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null) return Results.BadRequest(new { error = "Invalid id." });

        // Who's favoriting is resolved from the authenticated session, not a client-supplied
        // value — same reasoning as the ebook/TV/movie favorite endpoints.
        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner == null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        try
        {
            metadata.SetFavorite("audiobook", safeId, owner, request.Favorited);
            var updated = metadata.Get("audiobook", safeId);
            return Results.Ok(new { success = true, favorites = updated?.Favorites ?? new List<string>() });
        }
        catch (Exception ex)
        {
            Log.Warning("[Audiobooks] Failed to save audiobook:{Id} favorite: {Message}", safeId, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult HandleSetProgress(
        [FromRoute] string id, [FromBody] SetAudiobookProgressRequest request, HttpContext context, IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null) return Results.BadRequest(new { error = "Invalid id." });
        if (request.PositionSeconds < 0) return Results.BadRequest(new { error = "positionSeconds must be >= 0" });

        var owner = LibraryHelpers.ResolveUserDisplayName(context);
        if (owner == null)
            return Results.BadRequest(new { error = "Could not resolve the logged-in user." });

        try
        {
            metadata.SetProgress("audiobook", safeId, owner, request.PositionSeconds);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.Warning("[Audiobooks] Failed to save audiobook:{Id} progress: {Message}", safeId, ex.Message);
            return Results.Json(new { error = "Failed to save — please try again." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task HandleGetCover(HttpContext context, [FromRoute] string id, IAudiobookshelfService abs, IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        // A user-picked cover override takes priority over whatever Audiobookshelf
        // itself reports — same URL either way, so the frontend never needs to know
        // which source served it. Never writes back to Audiobookshelf.
        var overridePath = ResolveCoverOverridePath(safeId, metadata);
        if (overridePath is not null)
        {
            context.Response.Headers.CacheControl = "private, max-age=86400";
            context.Response.ContentType = ContentTypeForCoverFile(overridePath);
            await context.Response.SendFileAsync(overridePath, context.RequestAborted);
            return;
        }

        try
        {
            await CoverFetchGate.WaitAsync(context.RequestAborted);
            try
            {
                var result = await abs.GetCoverAsync(safeId, context.RequestAborted);
                // Covers are effectively immutable — let the browser cache them for a
                // day so revisiting the catalog doesn't re-proxy hundreds of images.
                if (result.StatusCode == StatusCodes.Status200OK)
                    context.Response.Headers.CacheControl = "private, max-age=86400";
                await RelayStreamAsync(context, result);
            }
            finally
            {
                CoverFetchGate.Release();
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Browser gave up on the image (scrolled away, page navigation) — routine, not an error.
        }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            Log.Warning("[Audiobooks] Cover proxy failed for {Id}: {Message}", safeId, ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    /// <summary>Downloads the given URL and stores it as this audiobook's cover
    /// override — same download/validate approach as the ebook library's
    /// cover-from-URL endpoint (LibraryCoverEndpoints.HandleUpdateCover), minus
    /// the meta.json bits, since audiobook state lives in MediaMetadataService.</summary>
    private static async Task<IResult> HandleSetCover(
        [FromRoute] string id,
        [FromBody] SetAudiobookCoverRequest request,
        HttpContext context,
        IHttpClientFactory httpFactory,
        IMediaMetadataService metadata)
    {
        var safeId = SanitizeId(id);
        if (safeId is null) return Results.BadRequest(new { error = "Invalid id." });

        if (request is null || string.IsNullOrWhiteSpace(request.CoverUrl))
            return Results.BadRequest(new { error = "coverUrl is required." });

        if (!Uri.TryCreate(request.CoverUrl, UriKind.Absolute, out var coverUri) ||
            (coverUri.Scheme != Uri.UriSchemeHttp && coverUri.Scheme != Uri.UriSchemeHttps))
            return Results.BadRequest(new { error = "coverUrl must be an http(s) URL." });

        byte[] coverBytes;
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = HttpTimeouts.LibraryHttpOperation;
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, coverUri);
            httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            httpRequest.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
            httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            httpRequest.Headers.Referrer = new Uri(coverUri.GetLeftPart(UriPartial.Authority));

            using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            response.EnsureSuccessStatusCode();
            coverBytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        }
        catch (Exception ex)
        {
            Log.Information("[Audiobooks] Failed to download cover for {Id}: {Message}", safeId, ex.Message);
            return Results.Problem("Failed to download cover image.");
        }

        if (!CoverLookupHelpers.TryGetImageSize(coverBytes, out var width, out var height))
            return Results.BadRequest(new { error = "Unsupported cover image format." });

        if (!CoverLookupHelpers.IsCoverSizeValid(width, height))
            return Results.BadRequest(new { error = "Cover image must be at least 100x100 pixels." });

        var coverExt = CoverLookupHelpers.DetermineImageExtension(coverUri.ToString(), coverBytes);
        var coverDir = StoragePaths.AudiobookCoverOverrideRoot();
        Directory.CreateDirectory(coverDir);

        foreach (var existing in Directory.GetFiles(coverDir, $"{safeId}.*"))
        {
            try { File.Delete(existing); } catch { /* ignore */ }
        }

        var coverFileName = $"{safeId}{coverExt}";
        await File.WriteAllBytesAsync(Path.Combine(coverDir, coverFileName), coverBytes, context.RequestAborted);

        metadata.SetCoverUrl("audiobook", safeId, coverFileName);
        Log.Information("[Audiobooks] Set custom cover for audiobook:{Id}", safeId);

        return Results.Ok(new { success = true });
    }

    private static string? ResolveCoverOverridePath(string safeId, IMediaMetadataService metadata)
    {
        var relative = metadata.Get("audiobook", safeId)?.CoverUrl;
        if (string.IsNullOrWhiteSpace(relative)) return null;

        var root = Path.GetFullPath(StoragePaths.AudiobookCoverOverrideRoot());
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string ContentTypeForCoverFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static async Task HandleStream(HttpContext context, [FromRoute] string id, [FromRoute] string ino, IAudiobookshelfService abs)
    {
        var safeId = SanitizeId(id);
        var safeIno = SanitizeId(ino);
        if (safeId is null || safeIno is null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        try
        {
            var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
            var result = await abs.StreamAudioFileAsync(safeId, safeIno, rangeHeader, context.RequestAborted);
            await RelayStreamAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Player paused/seeked/closed mid-stream — the browser aborts the
            // in-flight range request every time. Routine, not an error.
        }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            Log.Warning("[Audiobooks] Stream proxy failed for {Id}/{Ino}: {Message}", safeId, safeIno, ex.Message);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 502;
        }
    }

    /// <summary>Relays a proxied Audiobookshelf response (status, Content-Type,
    /// Content-Range/Content-Length when present, and body) straight through
    /// to the browser — same partial-content contract as the video library's
    /// local-file streaming, but the range slicing itself already happened
    /// upstream in Audiobookshelf rather than in this process.</summary>
    private static async Task RelayStreamAsync(HttpContext context, AudiobookshelfStreamResult result)
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

    /// <summary>Merges each item's recorded owners/genres/favorites into its raw
    /// Audiobookshelf JSON, matched by its own "id" — same idea as
    /// MediaLibraryEndpoints.ApplyMetadata, but keyed by Audiobookshelf's
    /// native string id rather than an int.</summary>
    private static void ApplyMetadata(JsonArray items, IMediaMetadataService metadataService)
    {
        var all = metadataService.GetAll();
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["id"] is null) continue;
            var meta = all.GetValueOrDefault($"audiobook:{obj["id"]!.GetValue<string>()}");
            obj["owners"] = new JsonArray((meta?.Owners ?? new List<string>()).Select(o => (JsonNode)o).ToArray());
            obj["customGenres"] = new JsonArray((meta?.Genres ?? new List<string>()).Select(g => (JsonNode)g).ToArray());
            obj["favorites"] = new JsonArray((meta?.Favorites ?? new List<string>()).Select(f => (JsonNode)f).ToArray());
            // Tells the frontend a cover override exists (served by HandleGetCover in
            // place of the Audiobookshelf proxy) even for items ABS itself has no cover for.
            obj["hasCustomCover"] = JsonValue.Create(meta?.CoverUrl != null);
            if (meta?.Progress is { Count: > 0 })
            {
                var progressObj = new JsonObject();
                foreach (var (owner, progress) in meta.Progress)
                    progressObj[owner] = progress.PositionSeconds;
                obj["progress"] = progressObj;
            }
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

    /// <summary>True for the failure shapes an unreachable/slow Audiobookshelf
    /// produces through the resilience pipeline: connection errors, the
    /// pipeline's per-attempt timeout (TimeoutRejectedException), and
    /// HttpClient's own timeout (TaskCanceledException). These become a
    /// friendly 502 rather than an unhandled 500.</summary>
    private static bool IsUpstreamFailure(Exception ex) =>
        ex is HttpRequestException
        or Polly.Timeout.TimeoutRejectedException
        or TaskCanceledException;

    /// <summary>Audiobookshelf ids are UUID-shaped strings — reject anything
    /// containing path-traversal or separator characters before it's used to
    /// build an outbound Audiobookshelf request or a MediaMetadataService key,
    /// same defensive intent as the filename traversal guards elsewhere in
    /// this codebase, adapted for an id forwarded into an upstream API call
    /// rather than a filesystem path.</summary>
    private static string? SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Any(c => c is '/' or '\\' or ':') || id.Contains(".."))
            return null;
        return id;
    }
}
