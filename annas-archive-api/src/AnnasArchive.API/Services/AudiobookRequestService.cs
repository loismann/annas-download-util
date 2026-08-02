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
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AsinLocks = new();

    /// <summary>
    /// Whether an ordinary request may let Listenarr pick the release itself,
    /// the way Radarr/Sonarr requests already do. It stays off until live
    /// release-matching accuracy has been measured on this stack, and it is
    /// overridden per request whenever the user stated a preference no
    /// automatic match can prove — see <see cref="ResolveAutoSearch"/>.
    /// </summary>
    private bool AutoSearchEnabled => configuration.GetValue("Listenarr:AutoSearch", false);

    public async Task<AudiobookRequestPreviewResponse> PreviewAsync(
        string ownerKey,
        string asin,
        string region,
        string? narratorPreference,
        string? languagePreference,
        CancellationToken ct)
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

        var autoSearch = ResolveAutoSearch(metadata, narratorPreference, languagePreference);
        var token = tokens.CreatePreview(ownerKey, asin, region, autoSearch.Allowed);
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
            autoSearch.Allowed,
            autoSearch.Reason,
            AlreadyRequested: await existingTask is not null);
    }

    public Task<AudiobookRequestResponse> ConfirmAsync(
        string ownerKey, string ownerLabel, string previewToken, CancellationToken ct)
    {
        var preview = tokens.ConsumePreview(ownerKey, previewToken)
            ?? throw new AudiobookRequestValidationException(
                "That request preview expired or belongs to another user. Review the edition again.");

        return AddRequestAsync(
            ownerKey, ownerLabel, preview.Asin, preview.Region, preview.AutoSearch, ct);
    }

    /// <summary>
    /// The single idempotent add path, shared by the reviewed single-book
    /// confirmation and the capped series workflow. Both authorize their own
    /// way in before calling it; the ASIN lock and the Listenarr preflight
    /// below are what guarantee one library entry per edition regardless of
    /// which path, or how many household members, arrive at once.
    /// </summary>
    public async Task<AudiobookRequestResponse> AddRequestAsync(
        string ownerKey,
        string ownerLabel,
        string asin,
        string region,
        bool autoSearch,
        CancellationToken ct)
    {
        var gate = AsinLocks.GetOrAdd(asin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ConfirmLockedAsync(ownerKey, ownerLabel, asin, region, autoSearch, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A stated narrator or language preference, or an abridged edition, can
    /// only be honoured by looking at the actual release — so those always
    /// fall back to manual review even once auto-search is enabled.
    /// </summary>
    private AutoSearchDecision ResolveAutoSearch(
        ListenarrAudibleBook metadata, string? narratorPreference, string? languagePreference)
    {
        if (!AutoSearchEnabled)
            return new AutoSearchDecision(false, "You will choose the release yourself.");
        if (!string.IsNullOrWhiteSpace(narratorPreference))
            return new AutoSearchDecision(false, "You named a narrator, so you will confirm the release yourself.");
        if (!string.IsNullOrWhiteSpace(languagePreference))
            return new AutoSearchDecision(false, "You named a language, so you will confirm the release yourself.");
        if (IsAbridged(metadata.BookFormat))
            return new AutoSearchDecision(false, "This edition is abridged, so you will confirm the release yourself.");

        return new AutoSearchDecision(true, "Listenarr will search and download the best matching release.");
    }

    private sealed record AutoSearchDecision(bool Allowed, string Reason);

    public async Task<AudiobookReleaseSearchResponse> SearchReleasesAsync(
        string ownerKey, int listenarrId, CancellationToken ct)
    {
        var request = store.GetByListenarrId(listenarrId)
            ?? throw new AudiobookRequestValidationException("That audiobook request was not found.");

        var upstream = await SearchIndexersWithFallbackAsync(request, ct);
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

    /// <summary>
    /// Builds the indexer query, then widens it if nothing came back.
    ///
    /// Measured against the live indexers on 2026-08-02: punctuation must be
    /// replaced with a space, not deleted. "Pandora's Star" returns nothing and
    /// "PandorasStar"/"Pandoras Star" also return nothing, but "Pandora Star"
    /// returns real releases — indexers tokenise on whitespace and never match
    /// across an apostrophe. Passing Audible's title through verbatim silently
    /// zeroed out every book with an apostrophe in its name.
    ///
    /// The author is narrowed to the first name and used only in the first
    /// attempt: release names spell authors inconsistently, so title alone is
    /// the more reliable fallback rather than a worse first guess.
    /// </summary>
    private async Task<IReadOnlyList<ListenarrIndexerSearchResult>> SearchIndexersWithFallbackAsync(
        AudiobookRequestRecord request, CancellationToken ct)
    {
        var title = NormalizeQuery(request.Title);
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var author = NormalizeQuery(FirstAuthor(request.Author));
        var attempts = string.IsNullOrWhiteSpace(author)
            ? [title]
            : new[] { $"{title} {author}", title };

        foreach (var attempt in attempts)
        {
            var results = await listenarr.SearchIndexersAsync(attempt, ct);
            if (results.Count > 0)
                return results;

            Log.Information(
                "[Listenarr] no releases for audiobook {ListenarrId} using query \"{Query}\"",
                request.ListenarrId, attempt);
        }

        return [];
    }

    /// <summary>Punctuation becomes whitespace, then runs of whitespace
    /// collapse. See <see cref="SearchIndexersWithFallbackAsync"/> for why
    /// deleting punctuation instead of replacing it does not work.</summary>
    public static string NormalizeQuery(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', new string(value
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The stored author snapshot is a comma-joined list; a release
    /// name almost never carries every co-author.</summary>
    public static string FirstAuthor(string? authors) => authors?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? string.Empty;

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

    /// <summary>
    /// Undoes a request. Cancel stops an active download; this removes the
    /// wanted entry entirely, which is the only way out of a request that
    /// never started one — a book with no findable release would otherwise sit
    /// on the page forever with no action that does anything.
    ///
    /// Deliberately narrower than library deletion: once a book has imported
    /// and reached Audiobookshelf it is media, not a request, and removing it
    /// stays the Audiobook Library page's job behind its hard-delete warning.
    ///
    /// The Listenarr entry only goes when the last requester withdraws.
    /// </summary>
    public async Task<AudiobookRequestRemovalResult> RemoveRequestAsync(
        string ownerKey, bool isAdmin, int listenarrId, CancellationToken ct)
    {
        EnsureMutationAuthority(ownerKey, isAdmin, listenarrId);
        var request = store.GetByListenarrId(listenarrId)
            ?? throw new AudiobookRequestValidationException("That audiobook request was not found.");

        if (!string.IsNullOrWhiteSpace(request.AbsItemId))
        {
            throw new AudiobookRequestValidationException(
                "This audiobook is already in your library. Remove it from the Audiobook Library page instead.");
        }

        // An in-flight download has to leave the client before the wanted
        // entry does, or the job keeps running against a book Listenarr no
        // longer knows about.
        var downloads = await listenarr.GetDownloadsAsync(ct);
        var active = downloads
            .Where(item => item.AudiobookId == listenarrId &&
                MapState(item.Status) is "Queued" or "Downloading" or "Paused")
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        if (active is not null)
            await listenarr.RemoveDownloadFromQueueAsync(active.Id, ct);

        var remaining = store.RemoveRequester(
            listenarrId, AudiobookRequestTokenStore.StableUserId(ownerKey));
        if (remaining > 0 && !isAdmin)
        {
            Log.Information(
                "[Listenarr] requester withdrawn from audiobook {ListenarrId}; {Remaining} requester(s) remain",
                listenarrId, remaining);
            return new AudiobookRequestRemovalResult(listenarrId, false, remaining);
        }

        await listenarr.DeleteFromLibraryAsync(listenarrId, ct);
        store.DeleteRequest(listenarrId);
        Log.Information(
            "[Listenarr] request removed entirely by user {UserId}, audiobook {ListenarrId}/{Asin}, downloadCanceled {Canceled}",
            SafeUser(ownerKey), listenarrId, request.Asin, active is not null);
        return new AudiobookRequestRemovalResult(listenarrId, true, 0);
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
        string asin,
        string region,
        bool autoSearch,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var metadata = await listenarr.GetAudibleMetadataAsync(asin, region, ct)
            ?? throw new AudiobookRequestValidationException("That audiobook edition is no longer available.");
        if (!string.Equals(metadata.Asin, asin, StringComparison.OrdinalIgnoreCase))
            throw new AudiobookRequestValidationException("Listenarr returned a different edition. Search again.");

        var existing = await listenarr.GetLibraryByAsinAsync(asin, ct);
        var alreadyExisted = existing is not null;
        // An edition already in Listenarr keeps whatever acquisition it
        // already has: a second requester must never kick off a second search.
        var searchStarted = false;
        if (existing is null)
        {
            var profileTask = listenarr.GetDefaultQualityProfileAsync(ct);
            var rootsTask = listenarr.GetRootFoldersAsync(ct);
            await Task.WhenAll(profileTask, rootsTask);
            if ((await rootsTask).Count == 0)
                throw new AudiobookRequestValidationException("Listenarr has no audiobook destination configured.");

            var addRequest = new ListenarrAddToLibraryRequest(
                ToLibraryMetadata(metadata, asin, region),
                Monitored: true,
                QualityProfileId: (await profileTask).Id,
                AutoSearch: autoSearch);
            try
            {
                var add = await listenarr.AddToLibraryAsync(addRequest, ct);
                existing = add.Audiobook;
                alreadyExisted = add.AlreadyExisted;
                searchStarted = autoSearch && !add.AlreadyExisted;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                existing = await ReconcileAmbiguousAddAsync(asin);
                if (existing is null)
                    throw;
                alreadyExisted = true;
            }
        }

        if (existing is null || existing.Id <= 0)
            throw new InvalidOperationException("Listenarr did not return the added audiobook.");

        var title = metadata.Title?.Trim() ?? existing.Title?.Trim() ?? asin;
        var authors = Names(metadata.Authors);
        var status = searchStarted ? "Searching" : "Monitored";
        var requesterAdded = store.SaveRequestAndRequester(
            existing,
            asin,
            string.IsNullOrWhiteSpace(metadata.Isbn) ? [] : [metadata.Isbn],
            title,
            string.Join(", ", authors),
            status,
            AudiobookRequestTokenStore.StableUserId(ownerKey),
            ownerLabel,
            timeProvider.GetUtcNow());

        Log.Information(
            "[Listenarr] request confirmed for user {UserId}, audiobook {ListenarrId}/{Asin}, existing {AlreadyExisted}, requesterAdded {RequesterAdded}, autoSearch {AutoSearch}, elapsed {ElapsedMs}ms",
            SafeUser(ownerKey), existing.Id, asin, alreadyExisted, requesterAdded, searchStarted,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new AudiobookRequestResponse(
            existing.Id,
            asin,
            title,
            status,
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
