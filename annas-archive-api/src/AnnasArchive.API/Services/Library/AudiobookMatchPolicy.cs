using System.Diagnostics.CodeAnalysis;

namespace AnnasArchive.API.Services.Library;

/// <summary>
/// The status strings a folder's sidecar records.
///
/// <para><b>These are an on-disk contract, not internal names.</b> They are
/// written into <c>.audiobook-enrichment.json</c> next to ~1000 real folders and
/// read back by both the next scan and
/// <c>AudiobookEnrichmentEndpoints.HandleGetStatus</c>, which matches them
/// case-sensitively out of raw JSON. Changing a value here silently reclassifies
/// every folder already on disk; add a new one instead.</para>
/// </summary>
public static class AudiobookSidecarStatus
{
    // matchStatus — what we know about which book this is.
    public const string Found = "found";
    public const string Unmatched = "unmatched";
    public const string Failed = "failed";

    // renameStatus — what has actually been done to the folder.
    public const string Renamed = "renamed";
    public const string Quarantined = "quarantined";
    public const string Pending = "pending";
    public const string SkippedNoMatch = "skippedNoMatch";
    public const string NeedsHumanReview = "needsHumanReview";

    // renameStatus, dry run only: a proposal, nothing on disk was touched.
    public const string DryRunProposed = "dryRunProposed";
    public const string DryRunProposedQuarantine = "dryRunProposedQuarantine";
}

/// <summary>What the scan should do with a folder, before spending anything on it.</summary>
public enum ScanDisposition
{
    /// <summary>Work out which book this is.</summary>
    Identify,

    /// <summary>Renamed or quarantined already; there is nothing left to do.</summary>
    AlreadyProcessed,

    /// <summary>Out of retries. Parked for a person, never auto-retried.</summary>
    NeedsHuman,

    /// <summary>Unmatched recently; leave it alone until the retry window opens.</summary>
    InRetryCooldown
}

/// <summary>A book the enrichment scan believes a folder actually contains, and
/// how much it believes it. <see cref="Source"/> is what decides the bar the
/// confidence has to clear — see <see cref="AudiobookMatchPolicy.RequiredConfidence"/>.</summary>
public sealed record AudiobookMatch(string Title, string? Author, int? Year, string Source, double Confidence);

/// <summary>
/// Every decision the audiobook scan makes about whether it has identified a
/// folder — separated from the scan itself because these rules, and only these
/// rules, stand between a wrong guess and <c>Directory.Move</c> on ~1000 folders
/// of somebody's library.
///
/// <para>The scan around this is unavoidably I/O: it walks a filesystem, calls
/// three remote services and renames directories. That is why none of this had a
/// test. Nothing here touches any of it — given a match and a couple of counters,
/// these are pure answers, so the part that can do damage is now the part that is
/// covered.</para>
/// </summary>
public static class AudiobookMatchPolicy
{
    /// <summary>Bar for a match scored against a real search hit.</summary>
    public const double CatalogueConfidence = 0.75;

    /// <summary>
    /// Bar for a match the model asserted about itself.
    ///
    /// <para>Higher than <see cref="CatalogueConfidence"/> because it is not the
    /// same kind of number. An OpenLibrary or Google Books confidence is measured
    /// — <see cref="TitleMatchScorer"/> scores the answer against the folder name
    /// that was searched for. GPT's is self-reported, with nothing corroborating
    /// it.</para>
    ///
    /// <para>Live testing showed exactly the failure that invites: a Warhammer
    /// 40k "Ciaphas Cain" novel called <em>Dead in the Water</em> was confidently
    /// replaced with Stuart Woods' unrelated same-titled book. Adding franchise
    /// context to the prompt stopped the substitutions themselves. This threshold
    /// is the second guard: GPT-4o only ever returns 0.8 or 0.9 in practice, and
    /// the wrong pre-fix matches clustered at 0.8 while the correct post-fix ones
    /// clustered at 0.9 — so the cutoff sits between two observed clusters rather
    /// than on a round number someone liked.</para>
    /// </summary>
    public const double AiConfidence = 0.9;

    /// <summary>Paid calls per folder, ever — not per scan.</summary>
    public const int MaxAiAttempts = 3;

    /// <summary>Match attempts before a folder is parked for a human. About five
    /// weekly retry cycles, given <see cref="UnmatchedRetryCooldown"/>.</summary>
    public const int MaxMatchAttempts = 5;

    /// <summary>How long an unmatched folder is left alone. The scan runs daily so
    /// new audiobooks are picked up quickly; without this, every stubbornly
    /// unmatched folder would re-query all three sources every day forever.</summary>
    public static readonly TimeSpan UnmatchedRetryCooldown = TimeSpan.FromDays(7);

    public const string OpenLibrarySource = "OpenLibrary";
    public const string GoogleBooksSource = "GoogleBooks";
    public const string AiSource = "AI";

    /// <summary>
    /// The bar this match has to clear, chosen by where it came from.
    ///
    /// <para><b>Only the two scored catalogue sources get the lower bar.</b>
    /// Everything else — the model, a source string this code does not recognise,
    /// a sidecar written before <c>matchSource</c> existed — gets
    /// <see cref="AiConfidence"/>. That default is deliberately the strict one:
    /// an unrecognised source is a source whose confidence has not been shown to
    /// mean anything, and the cost of being wrong in the safe direction is one
    /// extra lookup, while the cost of being wrong in the other direction is a
    /// misfiled book.</para>
    /// </summary>
    public static double RequiredConfidence(string? source) =>
        source is OpenLibrarySource or GoogleBooksSource ? CatalogueConfidence : AiConfidence;

    /// <summary>
    /// Whether a match is good enough to act on. The single rule; the scan asked
    /// this question in three places and phrased it three ways, one of which
    /// compared against <see cref="CatalogueConfidence"/> directly and so would
    /// have waved an 0.8 AI match through had it ever been handed one.
    ///
    /// <para><see cref="NotNullWhenAttribute"/> so a caller that has checked this
    /// does not then have to null-forgive the match it just validated — the
    /// check and the guarantee stay the same fact.</para>
    /// </summary>
    public static bool IsTrusted([NotNullWhen(true)] AudiobookMatch? match) =>
        match is not null && match.Confidence >= RequiredConfidence(match.Source);

    /// <summary>
    /// The best of the free-source answers, or null if none of them earned it.
    ///
    /// <para>Highest confidence wins outright: the sources are asked the same
    /// question and score on the same scale, so there is no reason to prefer
    /// whichever happened to be asked first.</para>
    /// </summary>
    public static AudiobookMatch? BestOf(params AudiobookMatch?[] candidates)
    {
        var best = candidates
            .Where(m => m is not null)
            .OrderByDescending(m => m!.Confidence)
            .FirstOrDefault();

        return IsTrusted(best) ? best : null;
    }

    /// <summary>
    /// Whether a match cached in a sidecar can still be used, or has to be
    /// recomputed.
    ///
    /// <para>The same bar as a fresh match, on purpose. A threshold or prompt
    /// change since the sidecar was written must not be able to grandfather in a
    /// no-longer-trusted result purely because it is already sitting on disk.</para>
    /// </summary>
    public static bool CanReuseCachedMatch(AudiobookMatch? cached) => IsTrusted(cached);

    /// <summary>
    /// Whether to spend a paid call on this folder.
    ///
    /// <para>Only when the free sources produced nothing usable, the folder has
    /// budget left, and there is a parsed title to ask about — a lookup with no
    /// title asks the model to invent one, which is how a confident wrong answer
    /// gets made.</para>
    /// </summary>
    public static bool ShouldTryAi(AudiobookMatch? freeSourceMatch, int aiAttempts, string? candidateTitle) =>
        !IsTrusted(freeSourceMatch)
        && aiAttempts < MaxAiAttempts
        && !string.IsNullOrWhiteSpace(candidateTitle);

    /// <summary>Whether this folder has run out of retries and needs a human.
    /// <paramref name="attemptsIncludingThisOne"/> counts the attempt that just
    /// failed.</summary>
    public static bool IsPermanentFailure(int attemptsIncludingThisOne) =>
        attemptsIncludingThisOne >= MaxMatchAttempts;

    /// <summary>
    /// Whether an unmatched folder is still inside its cooldown and should be
    /// left alone this pass. A folder never attempted has no cooldown to serve.
    /// </summary>
    public static bool IsInRetryCooldown(DateTime? lastAttemptedUtc, DateTime utcNow) =>
        lastAttemptedUtc is { } last && utcNow - last < UnmatchedRetryCooldown;

    // ── What to do with a folder before spending anything on it ──────────

    /// <summary>
    /// Reads a folder's sidecar and decides whether the scan has any work to do,
    /// taking the fields rather than the sidecar object so the decision is
    /// separable from how it is stored.
    /// </summary>
    ///
    /// <remarks>
    /// <para>The order is load-bearing. "Already done" is checked before "gave
    /// up", so a folder that failed several times and was later matched and
    /// renamed is left alone rather than being reported as a failure forever.</para>
    ///
    /// <para><b>Only <see cref="AudiobookSidecarStatus.Renamed"/> and
    /// <see cref="AudiobookSidecarStatus.Quarantined"/> count as done.</b> The two
    /// <c>dryRun…</c> statuses deliberately do not: a dry run writes a proposal
    /// and touches nothing, so treating it as processed would let a preview pass
    /// permanently suppress the real one — the scan ships with
    /// <c>DryRun = true</c> by default, so that would mean it never renames
    /// anything and looks like it is working.</para>
    /// </remarks>
    public static ScanDisposition Classify(
        string? renameStatus, string? matchStatus, DateTime? lastAttemptedUtc, DateTime utcNow)
    {
        if (renameStatus is AudiobookSidecarStatus.Renamed or AudiobookSidecarStatus.Quarantined)
            return ScanDisposition.AlreadyProcessed;

        if (matchStatus == AudiobookSidecarStatus.Failed)
            return ScanDisposition.NeedsHuman;

        if (matchStatus == AudiobookSidecarStatus.Unmatched && IsInRetryCooldown(lastAttemptedUtc, utcNow))
            return ScanDisposition.InRetryCooldown;

        return ScanDisposition.Identify;
    }
}
