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
        {
            foreach (var label in store.GetOwnerLabels(request.ListenarrId)
                         .Select(NormalizeOwner).Where(label => label is not null).Distinct())
                metadata.AddOwner("audiobook", match, label!);

            store.MarkReconciled(request.ListenarrId, match, timeProvider.GetUtcNow());
            Log.Information(
                "[Listenarr] reconciled audiobook {ListenarrId}/{Asin} to Audiobookshelf item {AbsItemId}",
                request.ListenarrId, request.Asin, match);
            return match;
        }

        var completedAt = download.CompletedAt ?? download.StartedAt;
        if (completedAt <= timeProvider.GetUtcNow().AddSeconds(-30))
            await RequestDebouncedScanAsync(ct);
        return null;
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
            if (node is not JsonObject item || IsMissing(item)) continue;
            var id = item["id"]?.ToString();
            var media = item["media"] as JsonObject;
            var book = media?["metadata"] as JsonObject;
            if (string.IsNullOrWhiteSpace(id) || book is null) continue;

            var asin = First(book, "asin", "audibleAsin") ?? First(item, "asin", "audibleAsin");
            if (!string.IsNullOrWhiteSpace(asin) &&
                string.Equals(asin, request.Asin, StringComparison.OrdinalIgnoreCase))
                return id;

            var title = book["title"]?.ToString();
            var author = book["authorName"]?.ToString();
            if (TitleMatchScorer.TokenSimilarity(request.Title, title) >= 0.98 &&
                TitleMatchScorer.CandidateAuthorScore(
                    SplitNames(request.Author), SplitNames(author)) >= 0.80)
                return id;
        }

        return null;
    }

    private static bool IsMissing(JsonObject item) =>
        item["isMissing"]?.GetValue<bool?>() == true ||
        item["media"]?["isMissing"]?.GetValue<bool?>() == true;

    private static string? First(JsonObject item, params string[] keys) =>
        keys.Select(key => item[key]?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string[] SplitNames(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NormalizeOwner(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        if (normalized.Contains("paul")) return "Paul";
        if (normalized.Contains("mom")) return "Mom";
        if (normalized.Contains("dad")) return "Dad";
        return null;
    }
}
