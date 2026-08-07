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
    private static readonly char[] InvalidPathChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
    private const double ConfidenceThreshold = 0.75;

    // AI-sourced matches get no independent real-catalog corroboration the
    // way OpenLibrary/GoogleBooks results do (those are scored against an
    // actual search hit via TitleMatchScorer) — GPT is just trusting its own
    // self-reported confidence. Live testing surfaced this failing exactly as
    // you'd expect: franchise tie-in fiction with a generic title (e.g. a
    // Warhammer 40k "Ciaphas Cain" novel called "Dead in the Water") getting
    // confidently substituted with an unrelated, more famous same-titled book
    // (Stuart Woods' "Dead in the Water"). Text-similarity can't distinguish
    // "same title, different book" on its own — adding franchise/series
    // context (BuildFranchiseContext) to the prompt fixed the substitutions
    // themselves (confirmed via live re-test: 0 wrong matches across a full
    // batch that previously had several). What's left is calibrating this
    // threshold: GPT-4o's self-reported confidence only ever comes back as
    // 0.8 or 0.9 in practice, no finer granularity, and empirically the wrong
    // pre-fix matches clustered at 0.8 while the post-fix correct ones
    // clustered at 0.9 — so 0.9 is the evidence-based cutoff, not a guess.
    private const double AiConfidenceThreshold = 0.9;
    private const int MaxAiAttempts = 3;
    private const string SidecarFileName = ".audiobook-enrichment.json";
    private const string DuplicateReviewFolderName = "_DuplicatesReview";

    // The scan itself runs daily (AiThrottlingConfiguration.AudiobookScanInterval) so
    // newly-added audiobooks get picked up quickly, but a stubbornly-unmatched item would
    // otherwise get re-queried against OpenLibrary/GoogleBooks/AI every single day forever —
    // this decouples "how often do we look for new work" from "how often do we retry the same
    // failure," so a persistent backlog costs roughly the same as the old weekly cadence while
    // new content is still daily-fresh.
    private static readonly TimeSpan UnmatchedRetryCooldown = TimeSpan.FromDays(7);

    // Roughly 5 weekly retry cycles (given UnmatchedRetryCooldown = 7 days) before giving up
    // permanently and flagging for human review — bounds how long a truly-unmatchable item
    // keeps consuming free-source queries (and, rarer, capped separately by MaxAiAttempts,
    // paid AI calls) instead of retrying forever with no path to a terminal state.
    private const int MaxMatchAttempts = 5;

    private static double RequiredConfidence(string source) =>
        source == "AI" ? AiConfidenceThreshold : ConfidenceThreshold;

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

    private async Task<ProcessResult> ProcessBookFolderAsync(
        string folderPath, ScanRateLimitState rateLimits, AudiobookScanOptions options, CancellationToken token)
    {
        var sidecarPath = Path.Combine(folderPath, SidecarFileName);
        var sidecar = await LoadSidecarAsync(sidecarPath, token);

        Log.Information("[AudiobookEnrichment] ── {Folder}", folderPath);

        if (sidecar?.RenameStatus is "renamed" or "quarantined")
        {
            Log.Information("[AudiobookEnrichment]   Already processed previously — skipping.");
            return ProcessResult.Skipped;
        }

        if (sidecar?.MatchStatus == "failed")
        {
            Log.Information("[AudiobookEnrichment]   Permanently failed after {Attempts} attempts — needs human review, not retrying.", sidecar.MatchAttempts);
            return ProcessResult.Failed;
        }

        if (sidecar?.MatchStatus == "unmatched" && sidecar.LastAttemptedUtc is { } lastAttempt
            && DateTime.UtcNow - lastAttempt < UnmatchedRetryCooldown)
        {
            Log.Information("[AudiobookEnrichment]   Unmatched, last tried {Ago:g} ago (< {Cooldown:g} cooldown) — skipping until next retry window.",
                DateTime.UtcNow - lastAttempt, UnmatchedRetryCooldown);
            return ProcessResult.Skipped;
        }

        if (!await IsFolderStableAsync(folderPath, token))
        {
            Log.Information("[AudiobookEnrichment]   Still changing (mid-transfer?) — skipping.");
            return ProcessResult.Skipped;
        }

        // Cached match from a prior dry run or a kill between matching and
        // renaming — skip straight to renaming, no re-querying.
        AudiobookMatch? match = sidecar?.MatchStatus == "found"
            ? new AudiobookMatch(sidecar.MatchedTitle!, sidecar.MatchedAuthor, sidecar.MatchedYear, sidecar.MatchSource ?? "cached", sidecar.MatchConfidence)
            : null;

        // A cached match must still clear today's bar — a threshold/prompt change since it was
        // written shouldn't be able to grandfather in a stale, no-longer-trusted result purely
        // because it's already on disk.
        if (match is not null && match.Confidence < RequiredConfidence(match.Source))
        {
            Log.Information("[AudiobookEnrichment]   Cached match ({Source}, confidence {Confidence}) no longer clears today's bar — recomputing.",
                match.Source, match.Confidence);
            match = null;
        }
        else if (match is not null)
        {
            Log.Information("[AudiobookEnrichment]   Using cached match: \"{Title}\" by {Author} (source {Source}, confidence {Confidence})",
                match.Title, match.Author ?? "?", match.Source, match.Confidence);
        }

        var aiAttempts = sidecar?.AiAttempts ?? 0;
        AudiobookCandidate? candidate = null;

        if (match is null)
        {
            var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
            candidate = AudiobookNameParser.ParseCandidate(folderName);
            Log.Information("[AudiobookEnrichment]   Candidate: title=\"{Title}\" author=\"{Author}\" narrator=\"{Narrator}\" year={Year}",
                candidate.Title ?? "(none)", candidate.Author ?? "(none)", candidate.Narrator ?? "(none)", candidate.Year?.ToString() ?? "(none)");

            match = await TryMatchAsync(candidate, rateLimits, token);

            if (match is null && aiAttempts < MaxAiAttempts && candidate.Title is not null)
            {
                aiAttempts++;
                var franchiseContext = BuildFranchiseContext(folderPath, ResolveAudiobooksRoot());
                Log.Information("[AudiobookEnrichment]   No free-source match cleared the bar — trying AI fallback (attempt {Attempt}/{Max}, franchiseContext=\"{Context}\")",
                    aiAttempts, MaxAiAttempts, franchiseContext ?? "(none)");
                match = await TryAiFallbackAsync(candidate, franchiseContext, rateLimits, token);
            }
        }

        if (match is null || match.Confidence < RequiredConfidence(match.Source))
        {
            var attempts = (sidecar?.MatchAttempts ?? 0) + 1;
            var permanentlyFailed = attempts >= MaxMatchAttempts;

            Log.Information("[AudiobookEnrichment]   → {Outcome} (best: {Best}, attempt {Attempts}/{Max})",
                permanentlyFailed ? "FAILED (permanent, needs human)" : "UNMATCHED",
                match is null ? "nothing cleared any threshold" : $"\"{match.Title}\" by {match.Author ?? "?"} (source {match.Source}, confidence {match.Confidence}, required {RequiredConfidence(match.Source)})",
                attempts, MaxMatchAttempts);

            await SaveSidecarAsync(sidecarPath, new AudiobookSidecar
            {
                OriginalPath = sidecar?.OriginalPath ?? folderPath,
                CandidateTitle = candidate?.Title ?? sidecar?.CandidateTitle,
                CandidateAuthor = candidate?.Author ?? sidecar?.CandidateAuthor,
                MatchStatus = permanentlyFailed ? "failed" : "unmatched",
                AiAttempts = aiAttempts,
                MatchAttempts = attempts,
                RenameStatus = permanentlyFailed ? "needsHumanReview" : "skippedNoMatch",
                LastAttemptedUtc = DateTime.UtcNow
            }, token);
            return permanentlyFailed ? ProcessResult.Failed : ProcessResult.Unmatched;
        }

        Log.Information("[AudiobookEnrichment]   → MATCHED: \"{Title}\" by {Author} (source {Source}, confidence {Confidence})",
            match.Title, match.Author ?? "?", match.Source, match.Confidence);

        var targetPath = ComputeTargetPath(match, ResolveAudiobooksRoot());
        var isSelfCollision = string.Equals(targetPath, folderPath, StringComparison.Ordinal);
        var targetAlreadyExists = Directory.Exists(targetPath) && !isSelfCollision;

        if (targetAlreadyExists)
        {
            var quarantinePath = ComputeQuarantinePath(folderPath, ResolveAudiobooksRoot());
            Log.Warning("[AudiobookEnrichment]   → DUPLICATE: would resolve to {Target}, which already exists. {Action} to {Quarantine}.",
                targetPath, options.DryRun ? "Would quarantine" : "Quarantining", quarantinePath);

            var quarantineSidecarPath = sidecarPath;
            if (!options.DryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
                Directory.Move(folderPath, quarantinePath);
                quarantineSidecarPath = Path.Combine(quarantinePath, SidecarFileName);
            }

            await SaveSidecarAsync(quarantineSidecarPath, new AudiobookSidecar
            {
                OriginalPath = folderPath,
                MatchStatus = "found",
                MatchedTitle = match.Title,
                MatchedAuthor = match.Author,
                MatchedYear = match.Year,
                MatchSource = match.Source,
                MatchConfidence = match.Confidence,
                AiAttempts = aiAttempts,
                RenameStatus = options.DryRun ? "dryRunProposedQuarantine" : "quarantined",
                CollisionTarget = targetPath
            }, token);
            return ProcessResult.Quarantined;
        }

        if (options.DryRun)
        {
            await SaveSidecarAsync(sidecarPath, new AudiobookSidecar
            {
                OriginalPath = folderPath,
                MatchStatus = "found",
                MatchedTitle = match.Title,
                MatchedAuthor = match.Author,
                MatchedYear = match.Year,
                MatchSource = match.Source,
                MatchConfidence = match.Confidence,
                AiAttempts = aiAttempts,
                RenameStatus = "dryRunProposed",
                RenamedTo = Path.GetRelativePath(ResolveAudiobooksRoot(), targetPath)
            }, token);
            Log.Information("[AudiobookEnrichment] DRY RUN: would rename '{Old}' -> '{New}' (confidence {Confidence}, source {Source})",
                folderPath, targetPath, match.Confidence, match.Source);
            return ProcessResult.Renamed;
        }

        // Step 1: write the "intent" sidecar at the OLD location first — if the process dies
        // right here, the next scan sees this cached match and just proceeds to the move
        // again, no re-querying.
        await SaveSidecarAsync(sidecarPath, new AudiobookSidecar
        {
            OriginalPath = folderPath,
            MatchStatus = "found",
            MatchedTitle = match.Title,
            MatchedAuthor = match.Author,
            MatchedYear = match.Year,
            MatchSource = match.Source,
            MatchConfidence = match.Confidence,
            AiAttempts = aiAttempts,
            RenameStatus = "pending"
        }, token);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        Directory.Move(folderPath, targetPath);

        var movedSidecarPath = Path.Combine(targetPath, SidecarFileName);
        await SaveSidecarAsync(movedSidecarPath, new AudiobookSidecar
        {
            OriginalPath = folderPath,
            MatchStatus = "found",
            MatchedTitle = match.Title,
            MatchedAuthor = match.Author,
            MatchedYear = match.Year,
            MatchSource = match.Source,
            MatchConfidence = match.Confidence,
            AiAttempts = aiAttempts,
            RenameStatus = "renamed",
            RenamedTo = Path.GetRelativePath(ResolveAudiobooksRoot(), targetPath)
        }, token);

        Log.Information("[AudiobookEnrichment] Renamed '{Old}' -> '{New}' (confidence {Confidence}, source {Source})",
            folderPath, targetPath, match.Confidence, match.Source);

        return ProcessResult.Renamed;
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

    private sealed record AudiobookMatch(string Title, string? Author, int? Year, string Source, double Confidence);

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

        var best = new[] { openLibrary, googleBooks }
            .Where(m => m is not null)
            .OrderByDescending(m => m!.Confidence)
            .FirstOrDefault();

        return best?.Confidence >= ConfidenceThreshold ? best : null;
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
            "OpenLibrary",
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
                    "GoogleBooks",
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

            return new AudiobookMatch(title, author, year, "AI", confidence);
        }
        catch (Exception ex)
        {
            rateLimits.RecordResult("AI", success: false);
            Log.Warning("[AudiobookEnrichment] AI fallback failed for '{Title}': {Message}", candidate.Title, ex.Message);
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
        var titleSegment = SanitizePathSegment(match.Year is int y ? $"{match.Title} ({y})" : match.Title);

        if (string.IsNullOrWhiteSpace(match.Author))
            return Path.Combine(root, titleSegment);

        var authorSegment = SanitizePathSegment(match.Author);
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
            .Select(SanitizePathSegment);
        return Path.Combine(new[] { root, DuplicateReviewFolderName }.Concat(sanitizedParts).ToArray());
    }

    private static string SanitizePathSegment(string value)
    {
        var cleaned = new string(value.Select(ch => InvalidPathChars.Contains(ch) ? ' ' : ch).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.TrimEnd('.', ' ');
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
            Log.Warning("[AudiobookEnrichment] Failed to read sidecar {Path}: {Message}", sidecarPath, ex.Message);
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
