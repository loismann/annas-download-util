using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services.PhotoPrint;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Endpoints;

public sealed record AddPrintItemRequest(
    string AssetId, string FileName, string SizeCode, int Quantity);

/// <summary>
/// Immich → CVS pickup prints. See
/// DOCS/features/google-photos-cvs-print-automation-spec.md.
///
/// Every route resolves its data through the caller's owner key, so one
/// household member can only ever reach their own runs — same rule as the
/// Spotify endpoints.
/// </summary>
public static class PhotoPrintEndpoints
{
    public static WebApplication MapPhotoPrintEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/photo-print")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/status", HandleStatus);
        group.MapGet("/sizes", HandleSizes);
        group.MapGet("/photos", HandleBrowsePhotos);
        group.MapGet("/photos/{assetId}/thumbnail", HandleThumbnail);

        group.MapPost("/runs", HandleCreateRun);
        group.MapGet("/runs", HandleListRuns);
        group.MapGet("/runs/{runId}", HandleGetRun);
        group.MapPost("/runs/{runId}/items", HandleAddItem);
        group.MapDelete("/runs/{runId}/items/{itemId}", HandleRemoveItem);
        group.MapPost("/runs/{runId}/prepare", HandlePrepare);
        group.MapPost("/runs/{runId}/cancel", HandleCancelRun);
        // TODO(photo-prints): HandleDownloadRun was mapped before it was written, which
        // broke the whole API build. Parked rather than guessed at — PhotoPrintRunService
        // has no download/zip surface yet, so what this returns is still a design call.
        // group.MapGet("/runs/{runId}/download", HandleDownloadRun);

        return app;
    }

    private static async Task<IResult> HandleStatus(
        IImmichService immich, IOptions<PhotoPrintConfiguration> config) =>
        Results.Ok(new
        {
            configured = immich.IsConfigured,
            reachable = await immich.IsReachableAsync(),
            pickupZip = config.Value.PickupZipCode,
            maxPrintsPerRun = config.Value.MaxPrintsPerRun
        });

    private static IResult HandleSizes() =>
        Results.Ok(PrintSize.Catalog.Select(size => new
        {
            code = size.Code,
            name = size.DisplayName,
            shortInches = size.ShortInches,
            longInches = size.LongInches,
            isSquare = size.IsSquare
        }));

    private static async Task<IResult> HandleBrowsePhotos(
        HttpContext context,
        IImmichService immich,
        DateTimeOffset? takenAfter,
        DateTimeOffset? takenBefore,
        bool? favoritesOnly,
        int? page,
        int? size,
        CancellationToken ct)
    {
        if (!immich.IsConfigured)
            return Results.Json(new { error = "Immich is not configured." }, statusCode: 503);

        try
        {
            var result = await immich.SearchAsync(new ImmichSearchQuery
            {
                TakenAfter = takenAfter,
                TakenBefore = takenBefore,
                FavoritesOnly = favoritesOnly ?? false,
                Page = page ?? 1,
                Size = size ?? 100
            }, ct);

            return Results.Ok(new
            {
                total = result.Total,
                nextPage = result.NextPage,
                items = result.Items.Select(a => new
                {
                    id = a.Id,
                    fileName = a.FileName,
                    takenAt = a.TakenAt,
                    width = a.Width,
                    height = a.Height,
                    isFavorite = a.IsFavorite
                })
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning("[PhotoPrint] Immich browse failed: {Message}", ex.Message);
            return Results.Json(new { error = "The photo library is temporarily unavailable." }, statusCode: 503);
        }
    }

    /// <summary>
    /// Proxies Immich's preview so the browser never needs the Immich API key —
    /// handing a long-lived library credential to client-side JavaScript to save
    /// one hop would be a poor trade.
    /// </summary>
    private static async Task<IResult> HandleThumbnail(
        string assetId, IImmichService immich, CancellationToken ct)
    {
        if (!immich.IsConfigured)
            return Results.NotFound();

        try
        {
            var stream = await immich.OpenThumbnailAsync(assetId, ct);
            return Results.Stream(stream, "image/jpeg");
        }
        catch (ImmichAssetNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Results.Json(new { error = "The photo library is temporarily unavailable." }, statusCode: 503);
        }
    }

    // ─── Runs ────────────────────────────────────────────────────────────

    private static IResult HandleCreateRun(HttpContext context, IPhotoPrintRunService runs) =>
        Owned(context, ownerKey => Results.Ok(new { runId = runs.CreateRun(ownerKey) }));

    private static IResult HandleListRuns(HttpContext context, IPhotoPrintOrderStore store) =>
        Owned(context, ownerKey => Results.Ok(store.ListRuns(ownerKey).Select(Describe)));

    private static IResult HandleGetRun(
        string runId, HttpContext context, IPhotoPrintOrderStore store) =>
        Owned(context, ownerKey =>
        {
            var run = store.GetRun(ownerKey, runId);
            if (run is null) return ApiResponse.NotFound("That print run was not found.");

            var items = store.ListItems(ownerKey, runId);
            return Results.Ok(new
            {
                run = Describe(run),
                totalPrints = store.TotalPrintCount(ownerKey, runId),
                items = items.Select(item => new
                {
                    itemId = item.ItemId,
                    assetId = item.ImmichAssetId,
                    fileName = item.SourceFileName,
                    sizeCode = item.SizeCode,
                    quantity = item.Quantity,
                    status = item.Status.ToString(),
                    effectiveDpi = item.EffectiveDpi,
                    belowQualityFloor = item.BelowQualityFloor,
                    error = item.LastError
                })
            });
        });

    private static IResult HandleAddItem(
        string runId, AddPrintItemRequest request, HttpContext context, IPhotoPrintRunService runs) =>
        Owned(context, ownerKey =>
        {
            if (string.IsNullOrWhiteSpace(request.AssetId))
                return ApiResponse.BadRequest("A photo is required.");

            runs.AddItem(
                ownerKey, runId, request.AssetId,
                string.IsNullOrWhiteSpace(request.FileName) ? "photo.jpg" : request.FileName,
                request.SizeCode, request.Quantity);

            return Results.Ok(new { runId });
        });

    private static IResult HandleRemoveItem(
        string runId, string itemId, HttpContext context, IPhotoPrintOrderStore store) =>
        Owned(context, ownerKey =>
        {
            store.RemoveItem(ownerKey, runId, itemId);
            return Results.Ok(new { runId, itemId });
        });

    private static async Task<IResult> HandlePrepare(
        string runId, HttpContext context, IPhotoPrintRunService runs, CancellationToken ct)
    {
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return ApiResponse.BadRequest("A signed-in user is required.");

        try
        {
            var outcome = await runs.PrepareAsync(ownerKey, runId, ct);
            return Results.Ok(new
            {
                runId,
                prepared = outcome.Prepared,
                failed = outcome.Failed,
                belowQualityFloor = outcome.BelowQualityFloor
            });
        }
        catch (PhotoPrintValidationException ex)
        {
            return ApiResponse.BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return ApiResponse.NotFound("That print run was not found.");
        }
    }

    private static IResult HandleCancelRun(
        string runId, HttpContext context, IPhotoPrintOrderStore store) =>
        Owned(context, ownerKey =>
        {
            store.UpdateRunStatus(ownerKey, runId, PrintRunStatus.Cancelled);
            return Results.Ok(new { runId, status = nameof(PrintRunStatus.Cancelled) });
        });

    // ─── Shared plumbing ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the caller's owner key and maps the two failures every
    /// owner-scoped route shares: an unknown run id and a rejected input. Without
    /// this the same six lines would repeat on each handler.
    /// </summary>
    private static IResult Owned(HttpContext context, Func<string, IResult> handler)
    {
        var ownerKey = UserHelpers.GetUserIdFromContext(context);
        if (string.IsNullOrWhiteSpace(ownerKey))
            return ApiResponse.BadRequest("A signed-in user is required.");

        try
        {
            return handler(ownerKey);
        }
        catch (PhotoPrintValidationException ex)
        {
            return ApiResponse.BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            // Covers "not yours" as well as "does not exist" — the store cannot
            // distinguish them, and neither should the response.
            return ApiResponse.NotFound("That print run was not found.");
        }
    }

    private static object Describe(PrintRun run) => new
    {
        runId = run.RunId,
        status = run.Status.ToString(),
        pickupZip = run.PickupZip,
        outputDirectory = run.OutputDirectory,
        screenshotPath = run.ScreenshotPath,
        error = run.LastError,
        createdAt = run.CreatedAt,
        updatedAt = run.UpdatedAt
    };
}
