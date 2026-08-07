using System.Text.Json.Nodes;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Library;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Bridges a completed Listenarr import to the existing Audiobookshelf catalog.
/// ABS's watcher gets the first chance; a single globally debounced, non-forced
/// scan is requested only after the import has been complete for 30 seconds.
/// </summary>
public sealed class AudiobookRequestReconciler(
    IAudiobookshelfService audiobookshelf,
    AudiobookRequestStore store,
    IMediaMetadataService metadata,
    TimeProvider timeProvider)
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;

    /// <summary>How often <see cref="LinkExistingAsync"/> may read the whole
    /// Audiobookshelf catalog. It runs on the library page's polling path, so
    /// without this it would be one full catalog read every 10 seconds.</summary>
    private static readonly TimeSpan LinkSweepInterval = TimeSpan.FromMinutes(1);
    private static DateTimeOffset _lastLinkSweepAt = DateTimeOffset.MinValue;

    public async Task<string?> ReconcileAsync(
        AudiobookRequestRecord request,
        ListenarrDownload download,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.AbsItemId))
            return request.AbsItemId;

        var items = await audiobookshelf.GetLibraryItemsAsync(ct);
        var match = FindMatch(request, items);
        if (match is not null)
            return Link(request, match);

        var completedAt = download.CompletedAt ?? download.StartedAt;
        if (completedAt <= timeProvider.GetUtcNow().AddSeconds(-30))
            await RequestDebouncedScanAsync(ct);
        return null;
    }

    /// <summary>
    /// Links requests whose book is already playable in Audiobookshelf but never
    /// completed a Listenarr import — a hand-placed file, a copy that predates the
    /// request, or a Listenarr rescan run outside this app. Without it those
    /// requests stay "not downloading yet" on the library page forever while the
    /// search page, which reads Audiobookshelf directly, offers to play them.
    ///
    /// Match-only on purpose: unlike <see cref="ReconcileAsync"/> it never asks
    /// Audiobookshelf to scan, because there is no completed import here to wait on
    /// and this runs on a polling path.
    /// </summary>
    /// <returns>How many requests were newly linked.</returns>
    public async Task<int> LinkExistingAsync(
        IReadOnlyList<AudiobookRequestRecord> requests, CancellationToken ct)
    {
        var unlinked = requests.Where(r => string.IsNullOrWhiteSpace(r.AbsItemId)).ToList();
        if (unlinked.Count == 0) return 0;

        var now = timeProvider.GetUtcNow();
        if (_lastLinkSweepAt > now - LinkSweepInterval) return 0;
        _lastLinkSweepAt = now;

        var items = await audiobookshelf.GetLibraryItemsAsync(ct);
        return unlinked.Count(request => FindMatch(request, items) is { } match && Link(request, match) is not null);
    }

    /// <summary>Records the Audiobookshelf item against the request and carries the
    /// requesters over as owners — without that tag the item is filtered out of the
    /// household library's owner view, so a linked-but-untagged book is playable and
    /// invisible at the same time.</summary>
    private string Link(AudiobookRequestRecord request, string absItemId)
    {
        foreach (var label in store.GetOwnerLabels(request.ListenarrId).Distinct())
            MediaOwnership.Assign(metadata, "audiobook", absItemId, label, "audiobook reconcile");

        store.MarkReconciled(request.ListenarrId, absItemId, timeProvider.GetUtcNow());
        Log.Information(
            "[Listenarr] reconciled audiobook {ListenarrId}/{Asin} to Audiobookshelf item {AbsItemId}",
            request.ListenarrId, request.Asin, absItemId);
        return absItemId;
    }

    private async Task RequestDebouncedScanAsync(CancellationToken ct)
    {
        if (_lastScanAt > timeProvider.GetUtcNow().AddMinutes(-2)) return;
        await ScanGate.WaitAsync(ct);
        try
        {
            if (_lastScanAt > timeProvider.GetUtcNow().AddMinutes(-2)) return;
            await audiobookshelf.ScanLibraryAsync(force: false, ct);
            _lastScanAt = timeProvider.GetUtcNow();
            Log.Information("[Listenarr] requested one non-forced, globally debounced Audiobookshelf scan");
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private static string? FindMatch(AudiobookRequestRecord request, JsonArray items)
    {
        foreach (var node in items)
        {
            if (node is not JsonObject item || AudiobookCatalogMatch.IsMissing(item)) continue;
            var id = item["id"]?.ToString();
            var media = item["media"] as JsonObject;
            var book = media?["metadata"] as JsonObject;
            if (string.IsNullOrWhiteSpace(id) || book is null) continue;

            var asin = First(book, "asin", "audibleAsin") ?? First(item, "asin", "audibleAsin");
            if (!string.IsNullOrWhiteSpace(asin) &&
                string.Equals(asin, request.Asin, StringComparison.OrdinalIgnoreCase))
                return id;

            if (AudiobookCatalogMatch.TitleAndAuthorMatch(
                    request.Title, AudiobookCatalogMatch.SplitNames(request.Author), book))
                return id;
        }

        return null;
    }

    private static string? First(JsonObject item, params string[] keys) =>
        keys.Select(key => item[key]?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

}
