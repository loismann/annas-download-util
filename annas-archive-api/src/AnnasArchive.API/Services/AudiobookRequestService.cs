using System.Collections.Concurrent;
using System.Diagnostics;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

public sealed class AudiobookRequestService(
    IListenarrService listenarr,
    AudiobookRequestStore store,
    AudiobookRequestTokenStore tokens,
    AudiobookRequestReconciler reconciler,
    TimeProvider timeProvider)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AsinLocks = new();

    public async Task<AudiobookRequestPreviewResponse> PreviewAsync(
        string ownerKey, string asin, string region, CancellationToken ct)
    {
        var metadataTask = listenarr.GetAudibleMetadataAsync(asin, region, ct);
        var profileTask = listenarr.GetDefaultQualityProfileAsync(ct);
        var rootsTask = listenarr.GetRootFoldersAsync(ct);
        var existingTask = listenarr.GetLibraryByAsinAsync(asin, ct);
        await Task.WhenAll(metadataTask, profileTask, rootsTask, existingTask);

        var metadata = await metadataTask
            ?? throw new AudiobookRequestValidationException("That audiobook edition is no longer available in the catalog.");
        if (!string.Equals(metadata.Asin, asin, StringComparison.OrdinalIgnoreCase))
            throw new AudiobookRequestValidationException("Listenarr returned a different audiobook edition. Search again.");

        var roots = await rootsTask;
        if (roots.Count == 0)
            throw new AudiobookRequestValidationException("Listenarr has no audiobook destination configured.");

        var title = metadata.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new AudiobookRequestValidationException("This catalog edition has no usable title.");

        var token = tokens.CreatePreview(ownerKey, asin, region);
        return new AudiobookRequestPreviewResponse(
            token.Token,
            token.ExpiresAt,
            asin,
            title,
            Names(metadata.Authors),
            Names(metadata.Narrators),
            metadata.Language,
            metadata.BookFormat,
            IsAbridged(metadata.BookFormat),
            (await profileTask).Name ?? "Default profile",
            AutoSearch: false,
            AlreadyRequested: await existingTask is not null);
    }

    public async Task<AudiobookRequestResponse> ConfirmAsync(
        string ownerKey, string ownerLabel, string previewToken, CancellationToken ct)
    {
        var preview = tokens.ConsumePreview(ownerKey, previewToken)
            ?? throw new AudiobookRequestValidationException(
                "That request preview expired or belongs to another user. Review the edition again.");

        var gate = AsinLocks.GetOrAdd(preview.Asin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ConfirmLockedAsync(ownerKey, ownerLabel, preview, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AudiobookReleaseSearchResponse> SearchReleasesAsync(
        string ownerKey, int listenarrId, CancellationToken ct)
    {
        var request = store.GetByListenarrId(listenarrId)
            ?? throw new AudiobookRequestValidationException("That audiobook request was not found.");

        var query = string.IsNullOrWhiteSpace(request.Author)
            ? request.Title
            : $"{request.Title} {request.Author}";
        var upstream = await listenarr.SearchIndexersAsync(query, ct);
        var releases = upstream
            .Where(result => !string.IsNullOrWhiteSpace(result.DownloadReference) &&
                !string.IsNullOrWhiteSpace(result.Title))
            .Take(50)
            .Select(result =>
            {
                var token = tokens.CreateRelease(
                    ownerKey, listenarrId, request.Asin, result.DownloadReference!);
                return new AudiobookReleaseOption(
                    token.Token,
                    token.ExpiresAt,
                    result.Title!,
                    result.Source ?? "Indexer",
                    result.DownloadType ?? "Unknown",
                    result.Format,
                    result.Quality,
                    result.Language,
                    result.Size,
                    result.Seeders,
                    result.Leechers,
                    result.Grabs,
                    result.Files,
                    result.Score);
            })
            .ToList();

        return new AudiobookReleaseSearchResponse(
            listenarrId, request.Asin, request.Title, releases);
    }

    public async Task<AudiobookReleaseGrabResponse> GrabReleaseAsync(
        string ownerKey, int listenarrId, string selectionToken, CancellationToken ct)
    {
        var selection = tokens.ConsumeRelease(ownerKey, listenarrId, selectionToken)
            ?? throw new AudiobookRequestValidationException(
                "That release choice expired or belongs to another user. Search releases again.");
        var request = store.GetByListenarrId(listenarrId);
        if (request is null || !string.Equals(request.Asin, selection.Asin, StringComparison.OrdinalIgnoreCase))
            throw new AudiobookRequestValidationException("That release does not belong to this audiobook request.");

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await listenarr.SendToDownloadClientAsync(
                selection.DownloadReference, listenarrId, ct);
            if (string.IsNullOrWhiteSpace(response.DownloadId))
                throw new InvalidOperationException("Listenarr did not return a download identifier.");

            store.UpdateStatus(listenarrId, "Queued", null, timeProvider.GetUtcNow());
            Log.Information(
                "[Listenarr] release grab succeeded for user {UserId}, audiobook {ListenarrId}/{Asin}, download {DownloadId}, elapsed {ElapsedMs}ms",
                SafeUser(ownerKey), listenarrId, request.Asin, response.DownloadId,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new AudiobookReleaseGrabResponse(
                listenarrId, request.Asin, response.DownloadId, "Queued");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning(
                "[Listenarr] release grab outcome is unknown for user {UserId}, audiobook {ListenarrId}/{Asin}, elapsed {ElapsedMs}ms: {Message}",
                SafeUser(ownerKey), listenarrId, request.Asin,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            throw;
        }
    }

    public async Task<AudiobookRequestStatusResponse> GetStatusAsync(
        string ownerKey, bool isAdmin, int listenarrId, CancellationToken ct)
    {
        var request = store.GetByListenarrId(listenarrId)
            ?? throw new AudiobookRequestValidationException("That audiobook request was not found.");
        var downloads = await listenarr.GetDownloadsAsync(ct);
        var download = downloads
            .Where(item => item.AudiobookId == listenarrId)
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(request.AbsItemId))
            return BuildStatus(request, download, "InLibrary", ownerKey, isAdmin);
        if (download is null)
            return BuildStatus(request, null, request.Status, ownerKey, isAdmin);

        var state = MapState(download.Status);
        if (state == "ReadyToScan")
        {
            var absItemId = await reconciler.ReconcileAsync(request, download, ct);
            if (absItemId is not null)
            {
                request = store.GetByListenarrId(listenarrId) ?? request;
                state = "InLibrary";
            }
        }

        store.UpdateStatus(
            listenarrId,
            state,
            state is "Failed" or "ImportBlocked" ? SafeFailure(download) : null,
            timeProvider.GetUtcNow());
        request = store.GetByListenarrId(listenarrId) ?? request;
        return BuildStatus(request, download, state, ownerKey, isAdmin);
    }

    public async Task CancelAsync(
        string ownerKey, bool isAdmin, int listenarrId, bool removeFromClient, CancellationToken ct)
    {
        EnsureMutationAuthority(ownerKey, isAdmin, listenarrId);
        if (!removeFromClient)
            throw new AudiobookRequestValidationException(
                "Cancellation must remove the active download-client job.");
        var downloads = await listenarr.GetDownloadsAsync(ct);
        var download = downloads
            .Where(item => item.AudiobookId == listenarrId &&
                MapState(item.Status) is "Queued" or "Downloading" or "Paused")
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault()
            ?? throw new AudiobookRequestValidationException("There is no active download to cancel.");

        await listenarr.RemoveDownloadFromQueueAsync(download.Id, ct);
        store.UpdateStatus(listenarrId, "Canceled", null, timeProvider.GetUtcNow());
        Log.Information(
            "[Listenarr] request download canceled by user {UserId}, audiobook {ListenarrId}, download {DownloadId}",
            SafeUser(ownerKey), listenarrId, download.Id);
    }

    public async Task RetryImportAsync(
        string ownerKey, bool isAdmin, int listenarrId, CancellationToken ct)
    {
        EnsureMutationAuthority(ownerKey, isAdmin, listenarrId);
        var downloads = await listenarr.GetDownloadsAsync(ct);
        var download = downloads
            .Where(item => item.AudiobookId == listenarrId && MapState(item.Status) == "ImportBlocked")
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault()
            ?? throw new AudiobookRequestValidationException("There is no blocked import to retry.");

        await listenarr.RetryImportAsync(download.Id, ct);
        store.UpdateStatus(listenarrId, "Importing", null, timeProvider.GetUtcNow());
        Log.Information(
            "[Listenarr] import retry requested by user {UserId}, audiobook {ListenarrId}, download {DownloadId}",
            SafeUser(ownerKey), listenarrId, download.Id);
    }

    private async Task<AudiobookRequestResponse> ConfirmLockedAsync(
        string ownerKey,
        string ownerLabel,
        AudiobookRequestPreviewToken preview,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var metadata = await listenarr.GetAudibleMetadataAsync(preview.Asin, preview.Region, ct)
            ?? throw new AudiobookRequestValidationException("That audiobook edition is no longer available.");
        if (!string.Equals(metadata.Asin, preview.Asin, StringComparison.OrdinalIgnoreCase))
            throw new AudiobookRequestValidationException("Listenarr returned a different edition. Search again.");

        var existing = await listenarr.GetLibraryByAsinAsync(preview.Asin, ct);
        var alreadyExisted = existing is not null;
        if (existing is null)
        {
            var profileTask = listenarr.GetDefaultQualityProfileAsync(ct);
            var rootsTask = listenarr.GetRootFoldersAsync(ct);
            await Task.WhenAll(profileTask, rootsTask);
            if ((await rootsTask).Count == 0)
                throw new AudiobookRequestValidationException("Listenarr has no audiobook destination configured.");

            var addRequest = new ListenarrAddToLibraryRequest(
                ToLibraryMetadata(metadata, preview.Asin, preview.Region),
                Monitored: true,
                QualityProfileId: (await profileTask).Id,
                AutoSearch: false);
            try
            {
                var add = await listenarr.AddToLibraryAsync(addRequest, ct);
                existing = add.Audiobook;
                alreadyExisted = add.AlreadyExisted;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                existing = await ReconcileAmbiguousAddAsync(preview.Asin);
                if (existing is null)
                    throw;
                alreadyExisted = true;
            }
        }

        if (existing is null || existing.Id <= 0)
            throw new InvalidOperationException("Listenarr did not return the added audiobook.");

        var title = metadata.Title?.Trim() ?? existing.Title?.Trim() ?? preview.Asin;
        var authors = Names(metadata.Authors);
        var requesterAdded = store.SaveRequestAndRequester(
            existing,
            preview.Asin,
            string.IsNullOrWhiteSpace(metadata.Isbn) ? [] : [metadata.Isbn],
            title,
            string.Join(", ", authors),
            "Monitored",
            AudiobookRequestTokenStore.StableUserId(ownerKey),
            ownerLabel,
            timeProvider.GetUtcNow());

        Log.Information(
            "[Listenarr] request confirmed for user {UserId}, audiobook {ListenarrId}/{Asin}, existing {AlreadyExisted}, requesterAdded {RequesterAdded}, autoSearch false, elapsed {ElapsedMs}ms",
            SafeUser(ownerKey), existing.Id, preview.Asin, alreadyExisted, requesterAdded,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new AudiobookRequestResponse(
            existing.Id,
            preview.Asin,
            title,
            "Monitored",
            alreadyExisted,
            requesterAdded);
    }

    private async Task<ListenarrLibraryItem?> ReconcileAmbiguousAddAsync(string asin)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            return await listenarr.GetLibraryByAsinAsync(asin, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning("[Listenarr] could not reconcile ambiguous add for {Asin}: {Message}", asin, ex.Message);
            return null;
        }
    }

    private static ListenarrLibraryMetadata ToLibraryMetadata(
        ListenarrAudibleBook book, string asin, string region)
    {
        var series = book.Series?.Where(value => !string.IsNullOrWhiteSpace(value.Name)).ToList() ?? [];
        var publishedDate = book.PublishDate ?? book.ReleaseDate;
        var publishYear = DateTimeOffset.TryParse(publishedDate, out var parsed)
            ? parsed.Year.ToString()
            : null;
        return new ListenarrLibraryMetadata(
            asin,
            "Audible",
            region,
            book.Title?.Trim() ?? asin,
            book.Subtitle,
            Names(book.Authors),
            book.ImageUrl,
            publishYear,
            publishedDate,
            series.FirstOrDefault()?.Name,
            series.FirstOrDefault()?.Position,
            series.Select((value, index) => new ListenarrAudiobookSeriesMembership(
                value.Name,
                value.Position,
                value.Asin,
                IsPrimary: index == 0,
                SortOrder: index)).ToList(),
            book.Description,
            Names(book.Genres),
            [],
            Names(book.Narrators),
            string.IsNullOrWhiteSpace(book.Isbn) ? [] : [book.Isbn],
            book.Publisher,
            book.Language,
            book.LengthMinutes,
            book.BookFormat,
            Version: null,
            Explicit: book.Explicit ?? false,
            Abridged: IsAbridged(book.BookFormat));
    }

    private static IReadOnlyList<string> Names(IEnumerable<ListenarrAudibleAuthor>? values) =>
        values?.Select(value => value.Name?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList() ?? [];

    private static IReadOnlyList<string> Names(IEnumerable<ListenarrAudibleNarrator>? values) =>
        values?.Select(value => value.Name?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList() ?? [];

    private static IReadOnlyList<string> Names(IEnumerable<ListenarrAudibleGenre>? values) =>
        values?.Select(value => value.Name?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList() ?? [];

    private static bool IsAbridged(string? format) =>
        string.Equals(format?.Trim(), "abridged", StringComparison.OrdinalIgnoreCase);

    private static string SafeUser(string ownerKey) =>
        AudiobookRequestTokenStore.StableUserId(ownerKey)[..12];

    private AudiobookRequestStatusResponse BuildStatus(
        AudiobookRequestRecord request,
        ListenarrDownload? download,
        string state,
        string ownerKey,
        bool isAdmin)
    {
        var canMutate = isAdmin || store.IsRequester(
            request.ListenarrId, AudiobookRequestTokenStore.StableUserId(ownerKey));
        return new AudiobookRequestStatusResponse(
            request.ListenarrId,
            request.Asin,
            request.Title,
            state,
            download?.Progress ?? 0,
            download?.Id,
            download?.TotalSize > 0 ? download.TotalSize : null,
            download is not null && download.DownloadedSize >= 0 ? download.DownloadedSize : null,
            download?.DownloadClientName,
            state is "Failed" or "ImportBlocked" ? SafeFailure(download) : null,
            state == "ImportBlocked" && download?.ImportBlockReason is { Length: > 0 } reason
                ? [$"Listenarr blocked the import: {reason}."]
                : [],
            request.AbsItemId,
            canMutate && (state is "Queued" or "Downloading" or "Paused"),
            canMutate && state == "ImportBlocked",
            request.UpdatedAt);
    }

    private void EnsureMutationAuthority(string ownerKey, bool isAdmin, int listenarrId)
    {
        if (isAdmin) return;
        var userId = AudiobookRequestTokenStore.StableUserId(ownerKey);
        if (!store.IsRequester(listenarrId, userId))
            throw new UnauthorizedAccessException("Only a requester or administrator can change this download.");
    }

    private static string MapState(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "queued" => "Queued",
        "downloading" => "Downloading",
        "paused" => "Paused",
        "completed" => "Processing",
        "processing" => "Processing",
        "importpending" => "Importing",
        "ready" => "ReadyToScan",
        "moved" => "ReadyToScan",
        "importblocked" => "ImportBlocked",
        "failed" => "Failed",
        _ => "Monitored"
    };

    private static string? SafeFailure(ListenarrDownload? download) =>
        MapState(download?.Status) switch
        {
            "ImportBlocked" => string.IsNullOrWhiteSpace(download?.ImportBlockReason)
                ? "Listenarr could not import this release."
                : $"Listenarr blocked the import: {download.ImportBlockReason}.",
            "Failed" => "The download failed in Listenarr.",
            _ => null
        };
}

public sealed class AudiobookRequestValidationException(string message) : Exception(message);
