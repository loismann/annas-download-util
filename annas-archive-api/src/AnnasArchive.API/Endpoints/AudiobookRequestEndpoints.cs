using System.Security.Claims;
using System.Text.RegularExpressions;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>Listenarr-backed audiobook discovery/request API. Phase 1 exposes
/// only the read-only integration status; search and mutations are added by
/// later gated phases.</summary>
public static class AudiobookRequestEndpoints
{
    private static readonly HashSet<string> SupportedRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "us", "uk", "ca", "au", "de", "fr", "it", "in", "jp", "es", "br"
    };
    private static readonly Regex AsinPattern = new("^[A-Z0-9]{10}$", RegexOptions.Compiled);
    private static readonly Regex OpaqueTokenPattern = new("^[A-F0-9]{64}$", RegexOptions.Compiled);

    public static WebApplication MapAudiobookRequestEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audiobook-requests/status", HandleStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/audiobook-requests/search", HandleSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests/preview", HandlePreview)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // Declared before the {listenarrId:int} routes for readability only —
        // the integer constraint already keeps "series" from matching them.
        app.MapPost("/api/audiobook-requests/series/preview", HandleSeriesPreview)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests/series/confirm", HandleSeriesConfirm)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests", HandleConfirm)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/audiobook-requests/{listenarrId:int}", HandleRequestStatus)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapGet("/api/audiobook-requests/{listenarrId:int}/releases", HandleReleaseSearch)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests/{listenarrId:int}/releases/{selectionToken}/grab", HandleReleaseGrab)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests/{listenarrId:int}/cancel", HandleCancel)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapPost("/api/audiobook-requests/{listenarrId:int}/retry-import", HandleRetryImport)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        app.MapDelete("/api/audiobook-requests/{listenarrId:int}", HandleRemove)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleStatus(IListenarrService listenarr, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await listenarr.GetIntegrationStatusAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Read-only status check failed: {Message}", ex.Message);
            return Results.Json(new
            {
                enabled = listenarr.IsEnabled,
                configured = listenarr.IsConfigured,
                reachable = false,
                ready = false,
                readOnlyGatePassed = false,
                gateFailures = new[] { "Listenarr is unavailable." }
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning("[Listenarr] Read-only status check timed out: {Message}", ex.Message);
            return Results.Json(new
            {
                enabled = listenarr.IsEnabled,
                configured = listenarr.IsConfigured,
                reachable = false,
                ready = false,
                readOnlyGatePassed = false,
                gateFailures = new[] { "Listenarr status check timed out." }
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleSearch(
        string? term,
        string? region,
        string? language,
        IListenarrService listenarr,
        AudiobookAvailabilityService availability,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook discovery is not enabled yet." });

        var query = term?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Results.BadRequest(new { error = "term must contain at least 2 characters." });
        if (query.Length > 200)
            return Results.BadRequest(new { error = "term must be 200 characters or fewer." });

        var requestedRegion = region?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(requestedRegion) && !SupportedRegions.Contains(requestedRegion))
            return Results.BadRequest(new { error = "Unsupported Audible region." });
        if (language?.Length > 50)
            return Results.BadRequest(new { error = "language must be 50 characters or fewer." });

        try
        {
            return Results.Ok(await availability.SearchAsync(query, requestedRegion, language, ct));
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Audiobook search failed for {Query}: {Message}", query, ex.Message);
            return Results.Json(new { error = "Audiobook search is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Audiobook search timed out for {Query}: {Message}", query, ex.Message);
            return Results.Json(new { error = "Audiobook search timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandlePreview(
        AudiobookRequestPreviewRequest request,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });

        var asin = request.Asin?.Trim().ToUpperInvariant();
        var region = request.Region?.Trim().ToLowerInvariant() ?? "us";
        if (string.IsNullOrWhiteSpace(asin) || !AsinPattern.IsMatch(asin))
            return Results.BadRequest(new { error = "A valid 10-character ASIN is required." });
        if (!SupportedRegions.Contains(region))
            return Results.BadRequest(new { error = "Unsupported Audible region." });

        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();

        var narrator = Preference(request.NarratorPreference);
        var preferredLanguage = Preference(request.LanguagePreference);
        if (narrator is { Length: > 200 } || preferredLanguage is { Length: > 50 })
            return Results.BadRequest(new { error = "That preference is too long." });

        try
        {
            return Results.Ok(await requests.PreviewAsync(
                ownerKey, asin, region, narrator, preferredLanguage, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Request preview failed for {Asin}: {Message}", asin, ex.Message);
            return Results.Json(new { error = "Listenarr is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Request preview timed out for {Asin}: {Message}", asin, ex.Message);
            return Results.Json(new { error = "The request preview timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandleConfirm(
        AudiobookRequestConfirmRequest request,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var token = request.PreviewToken?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(token) || !OpaqueTokenPattern.IsMatch(token))
            return Results.BadRequest(new { error = "A valid request preview token is required." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();
        var ownerLabel = context.User.FindFirstValue(ClaimTypes.Name) ?? "Household member";

        try
        {
            return Results.Ok(await requests.ConfirmAsync(ownerKey, ownerLabel, token, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Request confirmation failed: {Message}", ex.Message);
            return Results.Json(new
            {
                error = "Listenarr could not confirm the request. Search again to check whether it was added."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Request confirmation timed out: {Message}", ex.Message);
            return Results.Json(new
            {
                error = "The request outcome is uncertain. Search again before retrying."
            }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandleSeriesPreview(
        AudiobookSeriesPreviewRequest request,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookSeriesService series,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });

        var seriesAsin = request.SeriesAsin?.Trim().ToUpperInvariant();
        var region = request.Region?.Trim().ToLowerInvariant() ?? "us";
        if (string.IsNullOrWhiteSpace(seriesAsin) || !AsinPattern.IsMatch(seriesAsin))
            return Results.BadRequest(new { error = "A valid 10-character series ASIN is required." });
        if (!SupportedRegions.Contains(region))
            return Results.BadRequest(new { error = "Unsupported Audible region." });

        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();

        try
        {
            return Results.Ok(await series.PreviewAsync(ownerKey, seriesAsin, region, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Series preview failed for {SeriesAsin}: {Message}", seriesAsin, ex.Message);
            return Results.Json(new { error = "The series could not be loaded right now." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Series preview timed out for {SeriesAsin}: {Message}", seriesAsin, ex.Message);
            return Results.Json(new { error = "The series preview timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandleSeriesConfirm(
        AudiobookSeriesConfirmRequest request,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookSeriesService series,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });

        var token = request.PreviewToken?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(token) || !OpaqueTokenPattern.IsMatch(token))
            return Results.BadRequest(new { error = "A valid series preview token is required." });

        var asins = request.Asins ?? [];
        if (asins.Count == 0)
            return Results.BadRequest(new { error = "Select at least one book to request." });
        if (asins.Any(asin => !AsinPattern.IsMatch(asin?.Trim().ToUpperInvariant() ?? string.Empty)))
            return Results.BadRequest(new { error = "That selection contains an invalid ASIN." });

        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();
        var ownerLabel = context.User.FindFirstValue(ClaimTypes.Name) ?? "Household member";

        try
        {
            return Results.Ok(await series.ConfirmAsync(
                ownerKey, ownerLabel, context.User.IsInRole("Admin"),
                token, asins, request.ConfirmLarge, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Series confirmation failed: {Message}", ex.Message);
            return Results.Json(new
            {
                error = "Some books may not have been added. Preview the series again to see the current state."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? Preference(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<IResult> HandleReleaseSearch(
        int listenarrId,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        if (listenarrId <= 0)
            return Results.BadRequest(new { error = "A valid audiobook request is required." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();

        try
        {
            return Results.Ok(await requests.SearchReleasesAsync(ownerKey, listenarrId, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Release search failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "Release search is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Release search timed out for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "Release search timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandleReleaseGrab(
        int listenarrId,
        string selectionToken,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var token = selectionToken.Trim().ToUpperInvariant();
        if (listenarrId <= 0 || !OpaqueTokenPattern.IsMatch(token))
            return Results.BadRequest(new { error = "A valid release choice is required." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return Results.Unauthorized();

        try
        {
            return Results.Ok(await requests.GrabReleaseAsync(ownerKey, listenarrId, token, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Release grab failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new
            {
                error = "The download outcome is uncertain. Check request progress before trying again."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Listenarr] Release grab timed out for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new
            {
                error = "The download outcome is uncertain. Check request progress before trying again."
            }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> HandleRequestStatus(
        int listenarrId,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (listenarrId <= 0 || string.IsNullOrWhiteSpace(ownerKey))
            return Results.BadRequest(new { error = "A valid audiobook request is required." });
        try
        {
            return Results.Ok(await requests.GetStatusAsync(
                ownerKey, context.User.IsInRole("Admin"), listenarrId, ct));
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Status refresh failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "Request progress is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleCancel(
        int listenarrId,
        AudiobookRequestCancelRequest request,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (listenarrId <= 0 || string.IsNullOrWhiteSpace(ownerKey))
            return Results.BadRequest(new { error = "A valid audiobook request is required." });
        try
        {
            await requests.CancelAsync(
                ownerKey, context.User.IsInRole("Admin"), listenarrId, request.RemoveFromClient, ct);
            return Results.Ok(new { listenarrId, status = "Canceled" });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Cancellation failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "The cancellation outcome is uncertain. Refresh progress before retrying." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleRemove(
        int listenarrId,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (listenarrId <= 0 || string.IsNullOrWhiteSpace(ownerKey))
            return Results.BadRequest(new { error = "A valid audiobook request is required." });
        try
        {
            return Results.Ok(await requests.RemoveRequestAsync(
                ownerKey, context.User.IsInRole("Admin"), listenarrId, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Request removal failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "The request could not be removed. Refresh and try again." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleRetryImport(
        int listenarrId,
        HttpContext context,
        IListenarrService listenarr,
        AudiobookRequestService requests,
        CancellationToken ct)
    {
        if (!listenarr.IsEnabled)
            return Results.NotFound(new { error = "Audiobook requests are not enabled yet." });
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (listenarrId <= 0 || string.IsNullOrWhiteSpace(ownerKey))
            return Results.BadRequest(new { error = "A valid audiobook request is required." });
        try
        {
            await requests.RetryImportAsync(
                ownerKey, context.User.IsInRole("Admin"), listenarrId, ct);
            return Results.Ok(new { listenarrId, status = "Importing" });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (AudiobookRequestValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[Listenarr] Import retry failed for {ListenarrId}: {Message}", listenarrId, ex.Message);
            return Results.Json(new { error = "Listenarr could not retry the import." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
