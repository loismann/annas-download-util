using AnnasArchive.API.Data;
using AnnasArchive.API.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Services.PhotoPrint;

/// <summary>Raised when a request is invalid in a way the caller can fix.</summary>
public sealed class PhotoPrintValidationException(string message) : Exception(message);

public sealed record PreparationOutcome(int Prepared, int Failed, int BelowQualityFloor);

public interface IPhotoPrintRunService
{
    string CreateRun(string ownerKey);
    void AddItem(string ownerKey, string runId, string assetId, string fileName, string sizeCode, int quantity);
    Task<PreparationOutcome> PrepareAsync(string ownerKey, string runId, CancellationToken ct = default);
}

/// <summary>
/// Turns a selection of Immich photos into print-ready files on disk. Sits
/// between the store (what was chosen) and the preparation service (how one
/// photo becomes one print), and owns the run lifecycle across both.
/// </summary>
public sealed class PhotoPrintRunService : IPhotoPrintRunService
{
    private readonly IPhotoPrintOrderStore _store;
    private readonly IImmichService _immich;
    private readonly IPrintImagePreparationService _preparation;
    private readonly PhotoPrintConfiguration _config;

    public PhotoPrintRunService(
        IPhotoPrintOrderStore store,
        IImmichService immich,
        IPrintImagePreparationService preparation,
        IOptions<PhotoPrintConfiguration> config)
    {
        _store = store;
        _immich = immich;
        _preparation = preparation;
        _config = config.Value;
    }

    public string CreateRun(string ownerKey) => _store.CreateRun(ownerKey, _config.PickupZipCode);

    public void AddItem(
        string ownerKey, string runId, string assetId, string fileName, string sizeCode, int quantity)
    {
        if (!PrintSize.TryFromCode(sizeCode, out _))
            throw new PhotoPrintValidationException($"'{sizeCode}' is not a print size we offer.");
        if (quantity is < 1 or > 99)
            throw new PhotoPrintValidationException("Quantity must be between 1 and 99.");

        // The ceiling is enforced here, not in the browser: this is real money,
        // and quantity (not row count) is what it costs.
        var projected = _store.TotalPrintCount(ownerKey, runId) + quantity;
        if (projected > _config.MaxPrintsPerRun)
        {
            throw new PhotoPrintValidationException(
                $"That would make {projected} prints in one order; the limit is {_config.MaxPrintsPerRun}.");
        }

        _store.AddItem(ownerKey, runId, assetId, fileName, sizeCode, quantity);
    }

    /// <summary>
    /// Renders every pending item. One bad photo fails its own row and the run
    /// continues — re-picking an entire selection because a single source was
    /// unreadable would be a miserable way to lose ten minutes of choosing.
    /// </summary>
    public async Task<PreparationOutcome> PrepareAsync(
        string ownerKey, string runId, CancellationToken ct = default)
    {
        var run = _store.GetRun(ownerKey, runId)
            ?? throw new PhotoPrintValidationException("That print run was not found.");

        var items = _store.ListItems(ownerKey, runId);
        if (items.Count == 0)
            throw new PhotoPrintValidationException("Add at least one photo before preparing the order.");

        var outputDirectory = Path.Combine(ResolveOutputRoot(), runId);
        _store.SetRunOutput(ownerKey, runId, outputDirectory);
        _store.UpdateRunStatus(ownerKey, runId, PrintRunStatus.Preparing);

        int prepared = 0, failed = 0, soft = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Already rendered on an earlier attempt — this is what makes a
            // partially-failed run resumable instead of starting over.
            if (item.Status == PrintItemStatus.Prepared && File.Exists(item.PreparedPath))
            {
                prepared++;
                if (item.BelowQualityFloor) soft++;
                continue;
            }

            try
            {
                var size = PrintSize.FromCode(item.SizeCode);
                await using var original = await _immich.OpenOriginalAsync(item.ImmichAssetId, ct);

                var result = await _preparation.PrepareAsync(
                    original, item.SourceFileName, size, outputDirectory, ct);

                _store.MarkItemPrepared(
                    ownerKey, runId, item.ItemId,
                    result.OutputPath, result.EffectiveDpi, result.IsBelowQualityFloor);

                prepared++;
                if (result.IsBelowQualityFloor) soft++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "[PhotoPrint] Run {RunId}: could not prepare {File} at {Size}",
                    runId, item.SourceFileName, item.SizeCode);
                _store.MarkItemFailed(ownerKey, runId, item.ItemId, Describe(ex));
                failed++;
            }
        }

        // Ready only if everything rendered. A partial run stays Preparing so the
        // UI keeps offering a retry rather than presenting an incomplete order as
        // finished — which would silently under-order at the counter.
        _store.UpdateRunStatus(
            ownerKey, runId,
            failed == 0 ? PrintRunStatus.Ready : PrintRunStatus.Preparing,
            failed == 0 ? null : $"{failed} photo(s) could not be prepared.");

        return new PreparationOutcome(prepared, failed, soft);
    }

    private string ResolveOutputRoot() =>
        string.IsNullOrWhiteSpace(_config.OutputRoot)
            ? Path.Combine(Path.GetTempPath(), "photo-print")
            : _config.OutputRoot;

    private static string Describe(Exception ex) => ex switch
    {
        ImmichAssetNotFoundException => "The photo is no longer in Immich.",
        HttpRequestException => "Immich could not be reached.",
        TaskCanceledException => "Fetching the photo from Immich timed out.",
        _ => "The photo could not be processed."
    };
}
