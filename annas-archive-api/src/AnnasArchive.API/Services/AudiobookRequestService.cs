using System.Collections.Concurrent;
using System.Diagnostics;
using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Library;
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
    // Refcounted — one entry per ASIN ever requested, released when the last
    // confirmation for it finishes.
    private static readonly Helpers.KeyedLocks AsinLocks = new();

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

        // Ask the indexers before the book can be added, not after. Adding first and
        // discovering there is nothing to grab is how three monitored books ended up
        // permanently MISSING with an empty release picker behind them.
        var anyRelease = await HasAnyReleaseAsync(title, Names(metadata.Authors).FirstOrDefault(), ct);

        var token = tokens.CreatePreview(
            ownerKey, asin, region, autoSearch.Allowed, noReleasesFound: !anyRelease);
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
            AlreadyRequested: await existingTask is not null,
            ReleasesAvailable: anyRelease);
    }

    public Task<AudiobookRequestResponse> ConfirmAsync(
        string ownerKey, string ownerLabel, string previewToken, bool acceptNoReleases, CancellationToken ct)
    {
        var preview = tokens.ConsumePreview(ownerKey, previewToken)
            ?? throw new AudiobookRequestValidationException(
                "That request preview expired or belongs to another user. Review the edition again.");

        // Monitoring a book nothing currently carries is legitimate — it may be
        // indexed later — but it has to be a decision, not an accident, so the
        // warning has to come back acknowledged rather than merely displayed.
        if (preview.NoReleasesFound && !acceptNoReleases)
            throw new AudiobookRequestValidationException(
                "No releases for this book are available on your indexers right now. " +
                "Add it anyway to keep watching for one, or pick a different edition.");

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
        using var gate = await AsinLocks.AcquireAsync(asin, ct);
        return await ConfirmLockedAsync(ownerKey, ownerLabel, asin, region, autoSearch, ct);
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

    /// <summary>Ceiling on indexer round-trips for one release search — each costs
    /// about two seconds against the live indexers, and this is a page the household
    /// is waiting on.</summary>
    private const int MaxIndexerQueries = 5;

    /// <summary>
    /// Runs a ladder of progressively wider indexer queries and unions the results.
    ///
    /// Measured against the live indexers on 2026-08-02: punctuation must be
    /// replaced with a space, not deleted. "Pandora's Star" returns nothing and
    /// "PandorasStar"/"Pandoras Star" also return nothing, but "Pandora Star"
    /// returns real releases — indexers tokenise on whitespace and never match
    /// across an apostrophe. Passing Audible's title through verbatim silently
    /// zeroed out every book with an apostrophe in its name.
    ///
    /// Measured again on 2026-08-03, which is why this unions instead of stopping
    /// at the first non-empty attempt: Audible titles carry franchise decoration no
    /// release name repeats. "Star Wars The Jedi Academy Dark Apprentice" returns
    /// nothing while "Dark Apprentice" returns the book, and the widest attempt that
    /// does return something is often the least useful one ("Star Wars" alone
    /// returns a hundred soundtracks). Taking the union and ranking by
    /// <see cref="TitleMatchScorer.Coverage"/> means no attempt can mask a better
    /// one and the noise sinks to the bottom instead of crowding the list.
    ///
    /// The author is narrowed to the first name: release names spell co-authors
    /// inconsistently, so title alone is the more reliable widening step.
    /// </summary>
    private async Task<IReadOnlyList<ListenarrIndexerSearchResult>> SearchIndexersWithFallbackAsync(
        AudiobookRequestRecord request, CancellationToken ct)
    {
        var title = NormalizeQuery(request.Title);
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var author = NormalizeQuery(FirstAuthor(request.Author));
        var found = new Dictionary<string, ListenarrIndexerSearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var attempt in BuildQueryLadder(request.Title, title, author).Take(MaxIndexerQueries))
            await CollectAsync(attempt, found, request.ListenarrId, ct);

        // Last resort: the author's whole indexed catalog. Only worth the noise
        // when every title-shaped query came back empty, which is exactly when the
        // page would otherwise say "no releases" and leave nothing to choose from.
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(author))
            await CollectAsync(author, found, request.ListenarrId, ct);

        var wanted = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";
        return found.Values
            .OrderByDescending(result => TitleMatchScorer.Coverage(wanted, result.Title))
            .ToList();
    }

    /// <summary>
    /// Whether the indexers carry anything at all for a book that has not been
    /// requested yet — the gate on the preview step, so a book nothing carries can
    /// only be added deliberately.
    ///
    /// Deliberately the cheap answer rather than the thorough one. It stops at the
    /// first rung that returns anything, so the common case is one round trip
    /// (~2s) on a step someone is waiting on, and it skips the author-only rung
    /// that <see cref="SearchIndexersWithFallbackAsync"/> ends with — "this author
    /// has a catalog" is true for almost everyone and would wave through exactly
    /// the books this is meant to stop.
    ///
    /// What it cannot do is tell a right release from a wrong one. Measured
    /// 2026-08-03 against three unavailable books: two returned nothing and are
    /// caught here, one returned a single release for a different book in the same
    /// series and is not. Separating those needs a relevance threshold, and eight
    /// observed books is not enough to set one honestly — the release picker shows
    /// the real list, so a wrong single result is visible there instead.
    /// </summary>
    public async Task<bool> HasAnyReleaseAsync(string? rawTitle, string? authors, CancellationToken ct)
    {
        var title = NormalizeQuery(rawTitle);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var author = NormalizeQuery(FirstAuthor(authors));
        foreach (var attempt in BuildQueryLadder(rawTitle, title, author).Take(MaxIndexerQueries))
        {
            try
            {
                if ((await listenarr.SearchIndexersAsync(attempt, ct)).Count > 0)
                    return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // An indexer outage must not read as "this book does not exist" and
                // block a request that would otherwise be fine. Fail open.
                Log.Warning(ex, "[Listenarr] release probe for \"{Title}\" failed, assuming available", title);
                return true;
            }
        }

        Log.Information("[Listenarr] no releases on any indexer for \"{Title}\" by \"{Author}\"", title, author);
        return false;
    }

    private async Task CollectAsync(
        string query,
        Dictionary<string, ListenarrIndexerSearchResult> found,
        int listenarrId,
        CancellationToken ct)
    {
        var results = await listenarr.SearchIndexersAsync(query, ct);
        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.DownloadReference))
                found.TryAdd(result.DownloadReference!, result);
        }

        if (results.Count == 0)
            Log.Information(
                "[Listenarr] no releases for audiobook {ListenarrId} using query \"{Query}\"",
                listenarrId, query);
    }

    /// <summary>
    /// Whole title with author, whole title, then each colon-delimited part of the
    /// raw title on its own, longest first. Single-word parts are dropped: they are
    /// the franchise stem ("Exodus", "Star Wars") and match a hundred unrelated
    /// music releases rather than the book.
    /// </summary>
    public static IEnumerable<string> BuildQueryLadder(string? rawTitle, string title, string author)
    {
        if (!string.IsNullOrWhiteSpace(author))
            yield return $"{title} {author}";
        yield return title;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title };
        var parts = (rawTitle ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeQuery)
            .Where(part => part.Count(char.IsWhiteSpace) >= 1)
            .OrderByDescending(part => part.Length);

        foreach (var part in parts)
        {
            if (seen.Add(part))
                yield return part;
        }
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
            Log.Warning(ex, "[Listenarr] release grab outcome is unknown for user {UserId}, audiobook {ListenarrId}/{Asin}, elapsed {ElapsedMs}ms", SafeUser(ownerKey), listenarrId, request.Asin, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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

    /// <summary>
    /// Every audiobook this person has requested that has not yet landed in the
    /// library, so the library page can show it as in-flight. Without this the
    /// only view of a download is the search page's in-memory map, which is lost
    /// the moment you search again or navigate away.
    ///
    /// Listenarr's queue is fetched once for the whole list rather than per
    /// request — this runs on every library page load and polls while anything is
    /// active, so an N+1 here would be N+1 calls to Listenarr every 10 seconds.
    /// </summary>
    public async Task<IReadOnlyList<AudiobookRequestStatusResponse>> ListMineAsync(
        string ownerKey, bool isAdmin, CancellationToken ct)
    {
        var userId = AudiobookRequestTokenStore.StableUserId(ownerKey);
        var records = store.ListForUser(userId);
        if (records.Count == 0)
            return [];

        // A book can reach Audiobookshelf without ever completing a Listenarr
        // import. Catch those here (rate-limited inside the reconciler) rather
        // than leaving a ghost card that contradicts the search page.
        if (await reconciler.LinkExistingAsync(records, ct) > 0)
            records = store.ListForUser(userId);

        var downloads = await listenarr.GetDownloadsAsync(ct);
        var latestByAudiobook = downloads
            .Where(item => item.AudiobookId is not null)
            .GroupBy(item => item.AudiobookId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.StartedAt).First());

        var statuses = new List<AudiobookRequestStatusResponse>(records.Count);
        foreach (var record in records)
        {
            // Already reconciled into Audiobookshelf — the real library card
            // exists, so a ghost card next to it would just be a duplicate.
            if (!string.IsNullOrWhiteSpace(record.AbsItemId))
                continue;

            latestByAudiobook.TryGetValue(record.ListenarrId, out var download);
            var state = download is null ? record.Status : MapState(download.Status);

            // Only the match-only link pass above runs here. Scan-triggering
            // reconciliation stays in GetStatusAsync: doing it inside a list that
            // polls every 10 seconds would fire an Audiobookshelf scan per entry
            // per tick.
            statuses.Add(BuildStatus(record, download, state, ownerKey, isAdmin));
        }

        return statuses;
    }

    /// <summary>
    /// Hides one request from this person's own library view. Failed and
    /// import-blocked requests persist until dismissed precisely because they are
    /// the ones that otherwise sit unnoticed forever.
    /// </summary>
    public void Dismiss(string ownerKey, int listenarrId)
    {
        var userId = AudiobookRequestTokenStore.StableUserId(ownerKey);
        if (!store.SetDismissed(listenarrId, userId, dismissed: true, timeProvider.GetUtcNow()))
            throw new AudiobookRequestValidationException("That audiobook request was not found.");
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
            Log.Warning(ex, "[Listenarr] could not reconcile ambiguous add for {Asin}", asin);
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
