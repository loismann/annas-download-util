using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AnnasArchive.Core.Helpers;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Services.Library;
using Serilog;

using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Services;

public sealed record AudiobookScanOptions(bool DryRun, int? Limit);

public sealed record AudiobookScanSummary(
    int FoldersScanned, int Renamed, int Skipped, int DuplicatesQuarantined, int Unmatched, int Failed);

/// <summary>
/// Background service that identifies messy-named audiobook folders via OpenLibrary/Google
/// Books/AI lookups (modeled on the ebook LibraryWatcherService's enrichment chain) and renames
/// them to a clean, standardized "Author/Title (Year)/" format. Quarantines — never deletes —
/// duplicate copies of the same book into _DuplicatesReview for manual resolution rather than
/// silently overwriting anything.
///
/// Deliberately does NOT try to automatically detect and split "collection" folders (one folder
/// dumping several unrelated books' files together) — an automated filename-based detector was
/// built and tried live twice this session and both times badly false-positived on real
/// multi-chapter books (chapter-title similarity and chapter-numbering conventions turned out to
/// be far more varied in practice than any heuristic reliably covered). That judgment call is
/// made manually instead, folder by folder, with a human — see the NAS session notes. Once a
/// genuine collection folder is manually split into individual single-file folders, this service
/// picks each one up and matches/renames it exactly like any other book, no special handling
/// needed.
///
/// Three-state lifecycle per book folder, tracked via a per-folder sidecar JSON file: pending
/// (will be retried, subject to UnmatchedRetryCooldown), processed (renamed or quarantined), or
/// permanently failed after MaxMatchAttempts (needs a human, never auto-retried again).
///
/// Mutates the filesystem directly (renames/moves), which is why annas-archive-api has a
/// dedicated read-write mount to the audiobooks folder just for this — see docker-compose.yml.
/// Gated behind AudiobookWatcher:Enabled (default false) so it never starts touching ~1000
/// files unattended on a fresh deploy; also DryRun by default even when enabled.
///
/// Deliberately does not push matches into Audiobookshelf — that was tried (TriggerMatchAsync)
/// and proved unreliable (silent no-op whenever Audnexus doesn't have the book), and is made
/// redundant by deleting and recreating the Audiobookshelf library fresh once this rename pass
/// is done, letting its own mature full-library scan do the actual metadata matching.
/// </summary>
public class AudiobookEnrichmentService : BackgroundService
{
    private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wav", ".aac", ".wma" };
    private const string SidecarFileName = ".audiobook-enrichment.json";
    private const string DuplicateReviewFolderName = "_DuplicatesReview";

    // Every "is this good enough / should we retry / should we pay for a lookup"
    // decision below lives in AudiobookMatchPolicy, which is pure and tested.
    // What stays here is the I/O those answers drive.

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly IEnrichmentStatsService _statsService;
    private readonly IGoogleBooksService _googleBooks;
    private readonly ITokenUsageService _tokenUsage;

    public AudiobookEnrichmentService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        IEnrichmentStatsService statsService,
        IGoogleBooksService googleBooks,
        ITokenUsageService tokenUsage)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _statsService = statsService;
        _googleBooks = googleBooks;
        _tokenUsage = tokenUsage;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool>("AudiobookWatcher:Enabled", false);
        if (!enabled)
        {
            Log.Information("[AudiobookEnrichment] Disabled (AudiobookWatcher:Enabled=false) — not starting.");
            return;
        }

        var root = ResolveAudiobooksRoot();
        Directory.CreateDirectory(root);
        await _statsService.LoadAsync(stoppingToken);

        await RunScanAsync(DefaultOptionsFromConfig(), stoppingToken);

        var timer = new PeriodicTimer(AiThrottlingConfiguration.AudiobookScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanAsync(DefaultOptionsFromConfig(), stoppingToken);
        }
    }

    private AudiobookScanOptions DefaultOptionsFromConfig()
    {
        var dryRun = _configuration.GetValue<bool>("AudiobookWatcher:DryRun", true);
        var testLimit = _configuration.GetValue<int?>("AudiobookWatcher:TestLimit", null);
        return new AudiobookScanOptions(dryRun, testLimit);
    }

    /// <summary>Runs one full scan pass — invoked both by the internal daily
    /// timer and directly by the admin endpoint for manual/dry-run/subset
    /// triggering (bypassing the timer entirely).</summary>
    public async Task<AudiobookScanSummary> RunScanAsync(AudiobookScanOptions options, CancellationToken token)
    {
        var root = ResolveAudiobooksRoot();
        if (!Directory.Exists(root))
        {
            Log.Warning("[AudiobookEnrichment] Root {Root} does not exist — nothing to scan.", root);
            return new AudiobookScanSummary(0, 0, 0, 0, 0, 0);
        }

        Log.Information("[AudiobookEnrichment] Scan starting (dryRun={DryRun}, limit={Limit})", options.DryRun, options.Limit);

        var folders = FindBookFolders(root).ToList();
        if (options.Limit is int limit)
            folders = folders.Take(limit).ToList();

        Log.Information("[AudiobookEnrichment] Found {Count} book folders to consider", folders.Count);

        // Fresh per-scan, not shared service state — a manually-triggered run and the
        // background timer's own run shouldn't share rate-limit counters, and each new scan
        // deserves a clean try at a source that got skipped last time.
        var rateLimits = new ScanRateLimitState();

        var renamed = 0;
        var skipped = 0;
        var quarantined = 0;
        var unmatched = 0;
        var failed = 0;
        var processed = 0;

        foreach (var folder in folders)
        {
            if (token.IsCancellationRequested) break;

            var result = await ProcessBookFolderAsync(folder, rateLimits, options, token);
            switch (result)
            {
                case ProcessResult.Renamed: renamed++; break;
                case ProcessResult.Skipped: skipped++; break;
                case ProcessResult.Quarantined: quarantined++; break;
                case ProcessResult.Unmatched: unmatched++; break;
                case ProcessResult.Failed: failed++; break;
            }

            processed++;
            if (result != ProcessResult.Skipped)
            {
                await AiThrottlingConfiguration.ThrottleBetweenBooksAsync(token);
            }
            if (processed % AiThrottlingConfiguration.BatchSize == 0)
            {
                await AiThrottlingConfiguration.ThrottleLibraryBatchAsync(token);
            }
        }

        await _statsService.SaveAsync(token);

        var summary = new AudiobookScanSummary(folders.Count, renamed, skipped, quarantined, unmatched, failed);
        Log.Information("[AudiobookEnrichment] Scan complete: {@Summary}", summary);
        return summary;
    }

    private enum ProcessResult { Renamed, Skipped, Quarantined, Unmatched, Failed }

    /// <summary>What identifying a folder produced. <see cref="Candidate"/> is null
    /// when a cached match meant the folder name never had to be parsed, and
    /// <see cref="AiAttempts"/> is the running per-folder total including any call
    /// this pass just made.</summary>
    private sealed record Identification(AudiobookMatch? Match, AudiobookCandidate? Candidate, int AiAttempts);

    /// <summary>
    /// One folder, start to finish: decide whether to bother, work out which book
    /// it is, then either record the miss or act on the match.
    ///
    /// <para>Each step below is one of those sentences. They are separate methods
    /// because they fail differently — the first spends nothing, the second spends
    /// network calls and money, and only the third touches the filesystem.</para>
    /// </summary>
    private async Task<ProcessResult> ProcessBookFolderAsync(
        string folderPath, ScanRateLimitState rateLimits, AudiobookScanOptions options, CancellationToken token)
    {
        var sidecarPath = Path.Combine(folderPath, SidecarFileName);
        var sidecar = await LoadSidecarAsync(sidecarPath, token);

        Log.Information("[AudiobookEnrichment] ── {Folder}", folderPath);

        if (AlreadyDecided(sidecar) is { } decided)
            return decided;

        if (!await IsFolderStableAsync(folderPath, token))
        {
            Log.Information("[AudiobookEnrichment]   Still changing (mid-transfer?) — skipping.");
            return ProcessResult.Skipped;
        }

        var identified = await IdentifyAsync(folderPath, sidecar, rateLimits, token);

        return AudiobookMatchPolicy.IsTrusted(identified.Match)
            ? await ApplyMatchAsync(folderPath, sidecarPath, identified, options, token)
            : await RecordNoMatchAsync(folderPath, sidecarPath, sidecar, identified, token);
    }

    // ── Step 1: is there anything to do? ─────────────────────────────────

    /// <summary>Null when the folder still needs work; otherwise the result to
    /// report without spending a single call on it.</summary>
    private static ProcessResult? AlreadyDecided(AudiobookSidecar? sidecar)
    {
        switch (AudiobookMatchPolicy.Classify(
                    sidecar?.RenameStatus, sidecar?.MatchStatus, sidecar?.LastAttemptedUtc, DateTime.UtcNow))
        {
            case ScanDisposition.AlreadyProcessed:
                Log.Information("[AudiobookEnrichment]   Already processed previously — skipping.");
                return ProcessResult.Skipped;

            case ScanDisposition.NeedsHuman:
                Log.Information("[AudiobookEnrichment]   Permanently failed after {Attempts} attempts — needs human review, not retrying.",
                    sidecar!.MatchAttempts);
                return ProcessResult.Failed;

            case ScanDisposition.InRetryCooldown:
                Log.Information("[AudiobookEnrichment]   Unmatched, last tried {Ago:g} ago (< {Cooldown:g} cooldown) — skipping until next retry window.",
                    DateTime.UtcNow - sidecar!.LastAttemptedUtc!.Value, AudiobookMatchPolicy.UnmatchedRetryCooldown);
                return ProcessResult.Skipped;

            default:
                return null;
        }
    }

    // ── Step 2: which book is this? ──────────────────────────────────────

    /// <summary>
    /// The cached match, or a fresh lookup: free sources first, then the model
    /// only if nothing free cleared the bar. This is the only step that costs
    /// anything.
    /// </summary>
    private async Task<Identification> IdentifyAsync(
        string folderPath, AudiobookSidecar? sidecar, ScanRateLimitState rateLimits, CancellationToken token)
    {
        var aiAttempts = sidecar?.AiAttempts ?? 0;

        if (ReusableCachedMatch(sidecar) is { } cached)
            return new Identification(cached, Candidate: null, aiAttempts);

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        var candidate = AudiobookNameParser.ParseCandidate(folderName);
        Log.Information("[AudiobookEnrichment]   Candidate: title=\"{Title}\" author=\"{Author}\" narrator=\"{Narrator}\" year={Year}",
            candidate.Title ?? "(none)", candidate.Author ?? "(none)", candidate.Narrator ?? "(none)", candidate.Year?.ToString() ?? "(none)");

        var match = await TryMatchAsync(candidate, rateLimits, token);

        if (AudiobookMatchPolicy.ShouldTryAi(match, aiAttempts, candidate.Title))
        {
            aiAttempts++;
            var franchiseContext = BuildFranchiseContext(folderPath, ResolveAudiobooksRoot());
            Log.Information("[AudiobookEnrichment]   No free-source match cleared the bar — trying AI fallback (attempt {Attempt}/{Max}, franchiseContext=\"{Context}\")",
                aiAttempts, AudiobookMatchPolicy.MaxAiAttempts, franchiseContext ?? "(none)");
            match = await TryAiFallbackAsync(candidate, franchiseContext, rateLimits, token);
        }

        return new Identification(match, candidate, aiAttempts);
    }

    /// <summary>
    /// A match from a prior dry run, or from a kill between matching and renaming
    /// — reusable only if it still clears today's bar, so raising a threshold
    /// cannot grandfather in a result purely because it is already on disk.
    ///
    /// <para>A sidecar with no <c>matchSource</c> is one this code cannot vouch
    /// for, so it gets the strict bar (see
    /// <see cref="AudiobookMatchPolicy.RequiredConfidence"/>) and will usually be
    /// recomputed. That costs a lookup; trusting it could wave through an old 0.8
    /// model guess, which is the exact thing the AI threshold exists to stop.</para>
    /// </summary>
    private static AudiobookMatch? ReusableCachedMatch(AudiobookSidecar? sidecar)
    {
        if (sidecar?.MatchStatus != AudiobookSidecarStatus.Found)
            return null;

        var cached = new AudiobookMatch(
            sidecar.MatchedTitle!, sidecar.MatchedAuthor, sidecar.MatchedYear,
            sidecar.MatchSource ?? "cached", sidecar.MatchConfidence);

        if (!AudiobookMatchPolicy.CanReuseCachedMatch(cached))
        {
            Log.Information("[AudiobookEnrichment]   Cached match ({Source}, confidence {Confidence}) no longer clears today's bar — recomputing.",
                cached.Source, cached.Confidence);
            return null;
        }

        Log.Information("[AudiobookEnrichment]   Using cached match: \"{Title}\" by {Author} (source {Source}, confidence {Confidence})",
            cached.Title, cached.Author ?? "?", cached.Source, cached.Confidence);
        return cached;
    }

    // ── Step 3a: nothing cleared the bar ─────────────────────────────────

    private async Task<ProcessResult> RecordNoMatchAsync(
        string folderPath, string sidecarPath, AudiobookSidecar? sidecar, Identification identified, CancellationToken token)
    {
        var match = identified.Match;
        var attempts = (sidecar?.MatchAttempts ?? 0) + 1;
        var permanentlyFailed = AudiobookMatchPolicy.IsPermanentFailure(attempts);

        Log.Information("[AudiobookEnrichment]   → {Outcome} (best: {Best}, attempt {Attempts}/{Max})",
            permanentlyFailed ? "FAILED (permanent, needs human)" : "UNMATCHED",
            match is null ? "nothing cleared any threshold" : $"\"{match.Title}\" by {match.Author ?? "?"} (source {match.Source}, confidence {match.Confidence}, required {AudiobookMatchPolicy.RequiredConfidence(match.Source)})",
            attempts, AudiobookMatchPolicy.MaxMatchAttempts);

        await SaveSidecarAsync(sidecarPath, new AudiobookSidecar
        {
            OriginalPath = sidecar?.OriginalPath ?? folderPath,
            CandidateTitle = identified.Candidate?.Title ?? sidecar?.CandidateTitle,
            CandidateAuthor = identified.Candidate?.Author ?? sidecar?.CandidateAuthor,
            MatchStatus = permanentlyFailed ? AudiobookSidecarStatus.Failed : AudiobookSidecarStatus.Unmatched,
            AiAttempts = identified.AiAttempts,
            MatchAttempts = attempts,
            RenameStatus = permanentlyFailed ? AudiobookSidecarStatus.NeedsHumanReview : AudiobookSidecarStatus.SkippedNoMatch,
            LastAttemptedUtc = DateTime.UtcNow
        }, token);

        return permanentlyFailed ? ProcessResult.Failed : ProcessResult.Unmatched;
    }

    // ── Step 3b: act on the match ────────────────────────────────────────

    /// <summary>The only step that moves anything, and it moves nothing on a dry run.</summary>
    private async Task<ProcessResult> ApplyMatchAsync(
        string folderPath, string sidecarPath, Identification identified, AudiobookScanOptions options, CancellationToken token)
    {
        var match = identified.Match!;
        var root = ResolveAudiobooksRoot();

        Log.Information("[AudiobookEnrichment]   → MATCHED: \"{Title}\" by {Author} (source {Source}, confidence {Confidence})",
            match.Title, match.Author ?? "?", match.Source, match.Confidence);

        var targetPath = ComputeTargetPath(match, root);

        // A folder already sitting at its own computed target is not a duplicate
        // of itself — without this, a second scan would quarantine everything the
        // first one renamed.
        var isSelfCollision = string.Equals(targetPath, folderPath, StringComparison.Ordinal);

        if (Directory.Exists(targetPath) && !isSelfCollision)
            return await QuarantineDuplicateAsync(folderPath, sidecarPath, identified, targetPath, root, options, token);

        if (options.DryRun)
        {
            await SaveSidecarAsync(sidecarPath, FoundSidecar(folderPath, identified,
                AudiobookSidecarStatus.DryRunProposed, renamedTo: Path.GetRelativePath(root, targetPath)), token);

            Log.Information("[AudiobookEnrichment] DRY RUN: would rename '{Old}' -> '{New}' (confidence {Confidence}, source {Source})",
                folderPath, targetPath, match.Confidence, match.Source);
            return ProcessResult.Renamed;
        }

        // Write the intent at the OLD location first. If the process dies between
        // here and the move, the next scan finds this cached match and goes
        // straight to moving — no re-querying, nothing paid for twice.
        await SaveSidecarAsync(sidecarPath, FoundSidecar(folderPath, identified, AudiobookSidecarStatus.Pending), token);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        Directory.Move(folderPath, targetPath);

        await SaveSidecarAsync(Path.Combine(targetPath, SidecarFileName), FoundSidecar(folderPath, identified,
            AudiobookSidecarStatus.Renamed, renamedTo: Path.GetRelativePath(root, targetPath)), token);

        Log.Information("[AudiobookEnrichment] Renamed '{Old}' -> '{New}' (confidence {Confidence}, source {Source})",
            folderPath, targetPath, match.Confidence, match.Source);

        return ProcessResult.Renamed;
    }

    /// <summary>
    /// Two copies of the same book. Quarantined, never deleted and never merged —
    /// the folder keeps its messy name under <c>_DuplicatesReview</c> so a person
    /// can see what it was and where it came from.
    /// </summary>
    private async Task<ProcessResult> QuarantineDuplicateAsync(
        string folderPath, string sidecarPath, Identification identified,
        string targetPath, string root, AudiobookScanOptions options, CancellationToken token)
    {
        var quarantinePath = ComputeQuarantinePath(folderPath, root);
        Log.Warning("[AudiobookEnrichment]   → DUPLICATE: would resolve to {Target}, which already exists. {Action} to {Quarantine}.",
            targetPath, options.DryRun ? "Would quarantine" : "Quarantining", quarantinePath);

        // On a dry run the folder has not moved, so its sidecar stays where it is.
        var writeTo = sidecarPath;
        if (!options.DryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
            Directory.Move(folderPath, quarantinePath);
            writeTo = Path.Combine(quarantinePath, SidecarFileName);
        }

        var sidecar = FoundSidecar(folderPath, identified,
            options.DryRun ? AudiobookSidecarStatus.DryRunProposedQuarantine : AudiobookSidecarStatus.Quarantined);
        sidecar.CollisionTarget = targetPath;

        await SaveSidecarAsync(writeTo, sidecar, token);
        return ProcessResult.Quarantined;
    }

    /// <summary>
    /// The match fields every "we know what this is" sidecar carries. Written out
    /// by hand at four call sites before this, which is four chances for one of
    /// them to omit <c>MatchSource</c> — and a sidecar with no source is one the
    /// next scan cannot vouch for, so it gets re-queried from scratch.
    /// </summary>
    private static AudiobookSidecar FoundSidecar(
        string folderPath, Identification identified, string renameStatus, string? renamedTo = null)
    {
        var match = identified.Match!;
        return new AudiobookSidecar
        {
            OriginalPath = folderPath,
            MatchStatus = AudiobookSidecarStatus.Found,
            MatchedTitle = match.Title,
            MatchedAuthor = match.Author,
            MatchedYear = match.Year,
            MatchSource = match.Source,
            MatchConfidence = match.Confidence,
            AiAttempts = identified.AiAttempts,
            RenameStatus = renameStatus,
            RenamedTo = renamedTo
        };
    }

    // ── Rate-limit-aware backoff ────────────────────────────────────────────

    /// <summary>Per-scan (a fresh instance every RunScanAsync call, not shared service state)
    /// tracker that stops calling a source for the rest of the *current* scan once it's failed
    /// repeatedly in a row — on the assumption that's sustained rate limiting rather than
    /// one-off flakiness (which Polly's per-request retry/backoff on the named HttpClients
    /// already absorbs on its own). The next scheduled scan starts a clean counter and tries
    /// that source again; sidecars already make every retry idempotent, so this is the "back
    /// off for a long enough time" the existing daily cadence naturally provides, without a
    /// separate cooldown timer to build and maintain.</summary>
    private sealed class ScanRateLimitState
    {
        private const int MaxConsecutiveFailures = 5;
        private readonly Dictionary<string, int> _consecutiveFailures = new();
        private readonly HashSet<string> _tripped = new();

        public bool IsTripped(string source) => _tripped.Contains(source);

        public void RecordResult(string source, bool success)
        {
            if (success)
            {
                _consecutiveFailures[source] = 0;
                return;
            }

            var count = _consecutiveFailures.GetValueOrDefault(source) + 1;
            _consecutiveFailures[source] = count;
            if (count >= MaxConsecutiveFailures && _tripped.Add(source))
            {
                Log.Warning("[AudiobookEnrichment] {Source} failed {Count} times in a row this scan — skipping it for the rest of this run. Will retry fresh on the next scheduled scan.",
                    source, count);
            }
        }
    }

    // ── Matching ─────────────────────────────────────────────────────────

    private async Task<AudiobookMatch?> TryMatchAsync(AudiobookCandidate candidate, ScanRateLimitState rateLimits, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title))
            return null;

        var authors = string.IsNullOrWhiteSpace(candidate.Author) ? Array.Empty<string>() : new[] { candidate.Author };

        var openLibrary = await FetchOpenLibraryMatchAsync(candidate.Title, authors, rateLimits, token);
        Log.Information("[AudiobookEnrichment]   OpenLibrary: {Result}", DescribeMatch(openLibrary));
        _statsService.RecordCall("AudiobookOpenLibrary", openLibrary is not null, openLibrary?.Confidence);
        await AiThrottlingConfiguration.ThrottleAsync(token);

        var googleBooks = await FetchGoogleBooksMatchAsync(candidate.Title, authors, rateLimits, token);
        Log.Information("[AudiobookEnrichment]   GoogleBooks: {Result}", DescribeMatch(googleBooks));
        _statsService.RecordCall("AudiobookGoogleBooks", googleBooks is not null, googleBooks?.Confidence);
        await AiThrottlingConfiguration.ThrottleAsync(token);

        return AudiobookMatchPolicy.BestOf(openLibrary, googleBooks);
    }

    private static string DescribeMatch(AudiobookMatch? match) =>
        match is null ? "no match" : $"\"{match.Title}\" by {match.Author ?? "?"} ({match.Year?.ToString() ?? "?"}) — confidence {match.Confidence}";

    private async Task<AudiobookMatch?> FetchOpenLibraryMatchAsync(string title, string[] authors, ScanRateLimitState rateLimits, CancellationToken token)
    {
        if (rateLimits.IsTripped("OpenLibrary")) return null;

        // Named, resilience-wrapped client (retry + exponential backoff + circuit
        // breaker via .AddStandardResilience("OpenLibrary") in
        // ServiceConfiguration.cs) — same pattern GoogleBooks/OpenAI below use.
        var result = await OpenLibrarySearch.FindBestMatchAsync(
            _httpFactory.CreateClient("OpenLibrary"), title, authors, token);

        rateLimits.RecordResult("OpenLibrary", result.RequestSucceeded);

        if (!result.RequestSucceeded)
        {
            Log.Debug("[AudiobookEnrichment] OpenLibrary lookup failed for '{Title}'", title);
            return null;
        }

        if (result.BestDoc is not { } doc)
            return null;

        var candidateTitle = doc.TryGetProperty("title", out var t) ? t.GetString() : null;
        var candidateAuthors = OpenLibrarySearch.ExtractStringArray(doc, "author_name");

        return new AudiobookMatch(
            candidateTitle ?? title,
            candidateAuthors.FirstOrDefault(),
            OpenLibrarySearch.ExtractInt(doc, "first_publish_year"),
            AudiobookMatchPolicy.OpenLibrarySource,
            result.Confidence);
    }

    /// <summary>
    /// Scores Google Books candidates against the parsed folder name. The HTTP call
    /// itself now lives in <see cref="IGoogleBooksService.SearchVolumesAsync"/> — this
    /// used to hand-roll it, which is how the watcher's copy drifted into sending no
    /// API key. What stays here is what is genuinely local: the confidence scoring and
    /// the per-scan rate-limit bookkeeping.
    ///
    /// Freeform query rather than <c>intitle:</c>/<c>inauthor:</c> — more forgiving
    /// against messy candidate strings, same reasoning GoogleBooksService uses for its
    /// own description lookups.
    /// </summary>
    private async Task<AudiobookMatch?> FetchGoogleBooksMatchAsync(string title, string[] authors, ScanRateLimitState rateLimits, CancellationToken token)
    {
        if (rateLimits.IsTripped("GoogleBooks")) return null;

        var author = authors.FirstOrDefault();
        var query = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";

        var volumes = await _googleBooks.SearchVolumesAsync(query, maxResults: 5, token);

        // null means the request failed; an empty list means it answered with nothing.
        // Only the former should count against the rate limiter.
        rateLimits.RecordResult("GoogleBooks", success: volumes is not null);
        if (volumes is null)
        {
            Log.Debug("[AudiobookEnrichment] Google Books lookup failed for '{Title}'", title);
            return null;
        }

        AudiobookMatch? best = null;
        foreach (var volume in volumes)
        {
            var confidence = TitleMatchScorer.Confidence(title, volume.Title, authors, volume.Authors);
            if (best is null || confidence > best.Confidence)
            {
                best = new AudiobookMatch(
                    volume.Title ?? title,
                    volume.Authors.FirstOrDefault(),
                    volume.Year,
                    AudiobookMatchPolicy.GoogleBooksSource,
                    confidence);
            }
        }

        return best;
    }

    /// <summary>Joins up to the nearest 2 parent folder names (excluding the book's own leaf
    /// folder) as a franchise/series hint for the AI prompt — e.g. "Warhammer/Ciaphas
    /// Cain/Dead in the Water" yields "Warhammer > Ciaphas Cain". AudiobookNameParser only ever
    /// sees the leaf folder name, so without this the AI has no signal that a generically-
    /// titled book is actually tie-in fiction, and will happily substitute an unrelated, more
    /// well-known book that happens to share the same title.</summary>
    private static string? BuildFranchiseContext(string folderPath, string root)
    {
        var relative = Path.GetRelativePath(root, folderPath);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var parentParts = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
        if (parentParts.Length == 0) return null;

        var nearest = parentParts.Length > 2 ? parentParts[^2..] : parentParts;
        return string.Join(" > ", nearest);
    }

    private async Task<AudiobookMatch?> TryAiFallbackAsync(AudiobookCandidate candidate, string? franchiseContext, ScanRateLimitState rateLimits, CancellationToken token)
    {
        if (rateLimits.IsTripped("AI")) return null;

        try
        {
            var http = _httpFactory.CreateClient("OpenAI");
            var systemPrompt = "You are a book metadata librarian identifying audiobooks from messy folder names. " +
                "Be honest about uncertainty — a low confidence score is far more useful to the caller than a confident wrong guess. " +
                "Return ONLY valid JSON, no markdown.";

            var contextLine = franchiseContext is null
                ? ""
                : $@"

This folder is nested under: ""{franchiseContext}"". That strongly suggests the book belongs to that franchise, series, or imprint (e.g. media tie-in fiction, a game novelization, a specific book series) — prioritize identifying the actual entry within that context over an unrelated, more famous book that merely happens to share a similar or identical title. If you cannot identify a match consistent with this context, return confidence 0.3 or lower rather than substituting an unrelated book.";

            var userPrompt = $@"Messy folder name suggests: title=""{candidate.Title}"", author=""{candidate.Author ?? "unknown"}"", narrator=""{candidate.Narrator ?? "unknown"}"", year={candidate.Year?.ToString() ?? "unknown"}.{contextLine}

Identify the real book. Return JSON:
{{ ""title"": string|null, ""author"": string|null, ""year"": number|null, ""confidence"": number (0-1) }}

Confidence rubric — use the actual scale, don't default to round numbers:
0.9-1.0: you recognize this specific book/edition with high certainty, franchise context (if given) matches.
0.6-0.85: you recognize the general franchise/series but aren't fully certain this is the exact right entry.
0.3-0.55: plausible guess only, title is generic enough that multiple unrelated books could share it.
0.0-0.25: you don't recognize this at all.";

            var payload = new
            {
                model = "gpt-4o",
                input = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.1,
                max_output_tokens = 200
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, token);
            rateLimits.RecordResult("AI", response.IsSuccessStatusCode);
            _statsService.RecordCall("AudiobookGPT4", response.IsSuccessStatusCode);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[AudiobookEnrichment]   AI fallback HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            // Background enrichment: billed to the household, not to a person
            // who did not ask for it. Previously billed to nobody at all.
            //
            // Still a hand-rolled call rather than IAiResponsesCompletion: this
            // one reports its outcome to `rateLimits` and `_statsService`
            // *before* reading the body, and those two are what pace the
            // audiobook scanner. Moving it needs the shared client to surface
            // the status code, which no other caller wants.
            AiSpend.Record(_tokenUsage, AiSpend.BackgroundAccount, doc.RootElement);

            var text = ExtractResponseText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Warning("[AudiobookEnrichment]   AI fallback returned an empty response body.");
                return null;
            }

            var cleaned = AiText.StripCodeFences(text);

            using var resultDoc = JsonDocument.Parse(cleaned);
            var root = resultDoc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                Log.Information("[AudiobookEnrichment]   AI response: no title returned — treating as unmatched.");
                return null;
            }

            var author = root.TryGetProperty("author", out var a) ? a.GetString() : null;
            int? year = root.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.0;

            Log.Information("[AudiobookEnrichment]   AI response: title=\"{Title}\" author=\"{Author}\" year={Year} selfReportedConfidence={Confidence}",
                title, author ?? "(none)", year?.ToString() ?? "(none)", confidence);

            return new AudiobookMatch(title, author, year, AudiobookMatchPolicy.AiSource, confidence);
        }
        catch (Exception ex)
        {
            rateLimits.RecordResult("AI", success: false);
            Log.Warning(ex, "[AudiobookEnrichment] AI fallback failed for '{Title}'", candidate.Title);
            return null;
        }
    }

    private static string? ExtractResponseText(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                            return textProp.GetString();
                    }
                }
            }
        }
        return null;
    }

    // ── Folder discovery ─────────────────────────────────────────────────

    private static IEnumerable<string> FindBookFolders(string root)
    {
        foreach (var dir in EnumerateDirectoriesSafe(root))
        {
            var hasDirectAudio = HasDirectAudioFiles(dir);
            var subDirsWithAudio = EnumerateDirectoriesSafe(dir).Any(sub => ContainsAudioRecursive(sub));

            if (hasDirectAudio && subDirsWithAudio)
            {
                Log.Warning("[AudiobookEnrichment] Skipping mixed container (has both direct audio files and subfolders with audio): {Dir}", dir);
                continue;
            }

            if (hasDirectAudio)
            {
                yield return dir;
                continue;
            }

            foreach (var found in FindBookFolders(dir))
                yield return found;
        }
    }

    private static bool HasDirectAudioFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Any(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAudioRecursive(string dir)
    {
        if (HasDirectAudioFiles(dir)) return true;
        return EnumerateDirectoriesSafe(dir).Any(ContainsAudioRecursive);
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Where(d => !Path.GetFileName(d).Equals("@eaDir", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(d).StartsWith('.'));
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>Folder-level generalization of the ebook watcher's
    /// WaitForStableFileAsync — samples (file count, max mtime, total size)
    /// twice, ~1.5s apart; unstable means still mid-transfer, don't touch it
    /// this pass.</summary>
    private static async Task<bool> IsFolderStableAsync(string folderPath, CancellationToken token)
    {
        try
        {
            var first = SampleFolder(folderPath);
            await Task.Delay(1500, token);
            var second = SampleFolder(folderPath);
            return first == second;
        }
        catch
        {
            return false;
        }
    }

    private static (int Count, long MaxTicks, long TotalSize) SampleFolder(string folderPath)
    {
        var files = Directory.GetFiles(folderPath);
        var count = files.Length;
        long maxTicks = 0;
        long totalSize = 0;
        foreach (var f in files)
        {
            var info = new FileInfo(f);
            maxTicks = Math.Max(maxTicks, info.LastWriteTimeUtc.Ticks);
            totalSize += info.Length;
        }
        return (count, maxTicks, totalSize);
    }

    // ── Rename / quarantine path computation ────────────────────────────────

    private static string ComputeTargetPath(AudiobookMatch match, string root)
    {
        var titleSegment = SafeFileName.ForReadablePathSegment(match.Year is int y ? $"{match.Title} ({y})" : match.Title);

        if (string.IsNullOrWhiteSpace(match.Author))
            return Path.Combine(root, titleSegment);

        var authorSegment = SafeFileName.ForReadablePathSegment(match.Author);
        return Path.Combine(root, authorSegment, titleSegment);
    }

    /// <summary>Mirrors the original relative location under _DuplicatesReview/ rather than a
    /// flat dump, and keeps the original messy name intact — makes it obvious what the
    /// duplicate was and where it came from when a human reviews it later. Guaranteed unique
    /// per call since it's derived directly from the folder's own (unique) location in the tree.</summary>
    private static string ComputeQuarantinePath(string folderPath, string root)
    {
        var relative = Path.GetRelativePath(root, folderPath);
        var sanitizedParts = relative
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => SafeFileName.ForReadablePathSegment(p));
        return Path.Combine(new[] { root, DuplicateReviewFolderName }.Concat(sanitizedParts).ToArray());
    }

    // ── Sidecar persistence ──────────────────────────────────────────────

    private sealed class AudiobookSidecar
    {
        public int SchemaVersion { get; set; } = 1;
        public string? OriginalPath { get; set; }
        public string? CandidateTitle { get; set; }
        public string? CandidateAuthor { get; set; }
        public string? MatchStatus { get; set; } // "found" | "unmatched" | "failed"
        public string? MatchedTitle { get; set; }
        public string? MatchedAuthor { get; set; }
        public int? MatchedYear { get; set; }
        public string? MatchSource { get; set; } // "OpenLibrary" | "GoogleBooks" | "AI" | "cached"
        public double MatchConfidence { get; set; }
        public int MatchAttempts { get; set; }
        public int AiAttempts { get; set; }
        public string? RenameStatus { get; set; } // "pending" | "dryRunProposed" | "renamed" | "quarantined" | "dryRunProposedQuarantine" | "skippedNoMatch" | "needsHumanReview"
        public string? RenamedTo { get; set; }
        public string? CollisionTarget { get; set; }
        public DateTime? LastAttemptedUtc { get; set; } // set on unmatched — gates UnmatchedRetryCooldown
    }

    private static async Task<AudiobookSidecar?> LoadSidecarAsync(string sidecarPath, CancellationToken token)
    {
        if (!File.Exists(sidecarPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(sidecarPath, token);
            return JsonSerializer.Deserialize<AudiobookSidecar>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AudiobookEnrichment] Failed to read sidecar {Path}", sidecarPath);
            return null;
        }
    }

    private static readonly JsonSerializerOptions SidecarWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task SaveSidecarAsync(string sidecarPath, AudiobookSidecar sidecar, CancellationToken token)
    {
        // Must write camelCase to match AudiobookEnrichmentEndpoints.HandleGetStatus, which
        // reads these fields via raw JsonDocument.TryGetProperty("renameStatus", ...) — that
        // lookup is case-sensitive (unlike LoadSidecarAsync's case-insensitive deserialize
        // above), so writing PascalCase here would silently break /status entirely.
        var json = JsonSerializer.Serialize(sidecar, SidecarWriteOptions);
        await File.WriteAllTextAsync(sidecarPath, json, token);
    }

    private static string ResolveAudiobooksRoot() => Helpers.StoragePaths.AudiobooksRoot();
}
