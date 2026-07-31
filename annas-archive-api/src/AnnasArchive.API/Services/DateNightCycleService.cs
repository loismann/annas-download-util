using System.Text.Json;
using System.Text.Json.Nodes;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Data;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>One week's draw of pool movies and the household's votes on it.
/// One of these exists at a time — a new week's cycle overwrites the previous
/// one once it's due, per <see cref="DateNightCycleService.GetCurrentCycleAsync"/>.</summary>
public sealed record WeeklyCycle(
    string CycleId,                                     // Monday's date, "yyyy-MM-dd" (Hawaii)
    List<int> MovieIds,                                  // 1-5, drawn at issuance
    DateTime IssuedUtc,
    DateTime DeadlineUtc,                                // Sunday 23:59:59 Hawaii, stored as UTC
    string Status,                                        // Active | Resolved | NoMatch | Cancelled
    Dictionary<string, Dictionary<int, string>> Votes,   // person -> movieId -> "Up"|"Down"|"Never"
    Dictionary<string, DateTime> LastFlyerShownUtc,      // person -> last time the flyer was shown (once/day)
    int? ResolvedMovieId,
    DateTime? ResolvedUtc,
    ScheduleState? Schedule,                              // null until Resolved
    Dictionary<string, int>? FlyerReminderCounts = null); // person -> number of daily flyer prompts, capped at 3

/// <summary>One proposed day/time, in Hawaii local terms — kept as plain strings
/// rather than a DateTime so "Friday at 7pm" round-trips through JSON exactly as
/// typed, with no timezone ambiguity to introduce on the way in or out.</summary>
public sealed record ProposedSlot(string Date, string Time); // "yyyy-MM-dd", "HH:mm"

/// <summary>The propose -> (counter-propose)* -> lock handshake for one resolved
/// week's movie. Created the moment a cycle resolves; "first person" isn't a fixed
/// role — whoever proposes first is the proposer, and the other approves, counters
/// (which flips who's proposing), or cancels. Counter-proposing can ping-pong
/// indefinitely; only Approve or Cancel end it.</summary>
public sealed record ScheduleState(
    string Status,               // AwaitingProposal | AwaitingApproval | Locked | Cancelled | Concluded
    string? ProposedBy,
    List<ProposedSlot> ProposedSlots,
    ProposedSlot? LockedSlot,
    DateTime? LockedUtc,
    List<string> AcknowledgedBy, // who has seen the *current* proposal/cancellation
    string? CancelledBy,
    string DownloadStatus = "NotStarted", // NotStarted | Searching | Requested | Monitoring | Failed
    string? DownloadMessage = null,
    DateTime? DownloadUpdatedUtc = null,
    DateTime? PlaybackStartedUtc = null,
    DateTime? ConcludedUtc = null,
    string? ConclusionReason = null, // Watched | MissedStart | PlaybackWindowEnded
    string? ConclusionTitle = null,
    Dictionary<string, DateTime>? LastReminderShownUtc = null);

/// <summary>Whether a locked showtime is close enough to surface the countdown —
/// polled from the frontend, since this app has no push notifications to drive it.</summary>
public sealed record ShowtimeStatus(bool Imminent, int? MovieId, DateTime? ShowtimeUtc);

/// <summary>Permanent, cross-week state for one pool movie — the "four lists" collapsed
/// onto the movie they're about, since a movie can only be in one of them at a time.</summary>
public sealed record MovieListEntry(
    bool NeverShowAgain, DateTime? NeverShowAgainUtc,
    bool Watched, DateTime? WatchedUtc,
    DateTime? LastDisagreedUtc,   // cooling-off window is this + CoolingOff
    DateTime? LastApprovedUtc);   // informational only — mutual-approved-but-not-picked; doesn't gate eligibility

public sealed record SkipState(DateTime? SkipUntilUtc, string? SetBy, DateTime? SetUtc);

/// <summary>
/// The weekly draw, voting, and the four permanent lists (see DOCS/DATE_NIGHT_FEATURE.md).
///
/// Weekly issuance and the voting deadline are *lazy* — evaluated on whichever request
/// touches the cycle next. Locked-showtime cleanup is different because it changes
/// external Radarr state: <see cref="DateNightLifecycleService"/> advances it in the
/// background even when every browser is closed, while request-time checks remain as
/// a fallback and make the UI pivot immediately.
///
/// Week boundaries are Hawaii time, not server UTC — the server runs UTC (see
/// docker-compose.yml) but Mom and Dad experience the Monday-Sunday week in Hawaii.
/// Implemented as a fixed -10:00 offset rather than a named timezone lookup: Hawaii
/// never observes DST, so the fixed offset is exactly as correct and doesn't depend on
/// the container image having IANA tzdata installed. Incomplete voting remains open
/// through Sunday; each person receives at most three once-daily flyer prompts first.
/// </summary>
public class DateNightCycleService
{
    private const string CycleStateKey = "date-night:cycle";
    private const string ListsStateKey = "date-night:lists";
    private const string SkipStateKey = "date-night:skip";
    private const string LiveStateKey = "date-night:live";
    private const string SummaryReserveStateKey = "date-night:summary-reserve";

    /// <summary>Completely separate storage for the admin dry-run — a test cycle
    /// draws from the same pool and triggers the same real Radarr actions, but never
    /// touches the real household's cycle/lists, so testing can't consume this week's
    /// real draw or leave real movies in a cooldown/ban Mom and Dad didn't cause. See
    /// DOCS/DATE_NIGHT_FEATURE.md and REFACTORING_TODO.md.</summary>
    private const string TestCycleStateKey = "date-night:test-cycle";
    private const string TestListsStateKey = "date-night:test-lists";
    private const string TestSummaryReserveStateKey = "date-night:test-summary-reserve";

    /// <summary>The test cycle has no real week — one fixed id, since there's only
    /// ever one active dry run at a time, same as the real cycle only has one active
    /// week.</summary>
    private const string TestCycleId = "test";

    private static readonly TimeSpan HawaiiOffset = TimeSpan.FromHours(-10);

    public const int MaxFlyerReminderCount = 3;

    /// <summary>Public so the admin endpoint can compute "still cooling off" the same
    /// way issuance does, instead of re-hardcoding the 4 weeks.</summary>
    public static readonly TimeSpan CoolingOff = TimeSpan.FromDays(28); // 4 weeks

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> ValidVotes = new(StringComparer.OrdinalIgnoreCase) { "Up", "Down", "Never" };

    private readonly DateNightAvailabilityService _availability;
    private readonly DateNightSummaryService _summaries;
    private readonly AppDatabase _db;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Guards the read-evaluate-write sequence in <see cref="GetCurrentCycleAsync"/>
    /// against a race where two near-simultaneous requests both decide a new cycle is due
    /// and each issues (and persists) its own draw.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DateNightCycleService(
        DateNightAvailabilityService availability,
        DateNightSummaryService summaries,
        AppDatabase db,
        IServiceScopeFactory scopeFactory)
    {
        _availability = availability;
        _summaries = summaries;
        _db = db;
        _scopeFactory = scopeFactory;
    }

    private static DateTimeOffset NowHawaii => DateTimeOffset.UtcNow.ToOffset(HawaiiOffset);

    private static DateOnly MondayOf(DateTimeOffset hawaiiNow)
    {
        var date = DateOnly.FromDateTime(hawaiiNow.Date);
        var back = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-back);
    }

    private static DateTime ToUtc(DateOnly hawaiiDate, TimeOnly hawaiiTime) =>
        new DateTimeOffset(hawaiiDate.ToDateTime(hawaiiTime), HawaiiOffset).UtcDateTime;

    /// <summary>The full Monday-Sunday calendar week remains available for voting
    /// and scheduling. Sunday at 11:59:59 PM Hawaii time is the final fallback for
    /// an unfinished ballot.</summary>
    public static DateTime WeeklyDeadlineUtc(DateOnly monday) =>
        ToUtc(monday.AddDays(6), new TimeOnly(23, 59, 59));

    /// <summary>Advances and returns the current cycle: issues a fresh week if none is
    /// active for this week (and no skip is in effect), and resolves/cancels an active
    /// cycle whose deadline has passed. Every read of the cycle goes through this, so the
    /// state machine has exactly one place it moves forward.</summary>
    public async Task<WeeklyCycle?> GetCurrentCycleAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await AdvanceAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WeeklyCycle?> AdvanceAsync(CancellationToken ct)
    {
        var now = NowHawaii;
        var skip = GetSkip();
        if (skip.SkipUntilUtc is DateTime skipUntil && skipUntil > now.UtcDateTime)
            return null;

        var cycle = LoadCycle();
        var thisMonday = MondayOf(now);

        if (cycle is not null)
            cycle = await AdvanceShowtimeLifecycleAsync(cycle, isTest: false, ct);

        // Cycles written before the current policy used Thursday as their hard
        // cutoff, had no reminder counter, and could have a null scheduling state.
        // Upgrade the current week's record in place. Most importantly, this reopens
        // an incomplete cycle that the legacy Thursday rule already cancelled,
        // allowing the remaining weekend days to be used. A migrated/new cycle has
        // a non-null counter, so a genuine cancellation is never reopened.
        var needsReminderPolicyUpgrade = cycle?.FlyerReminderCounts is null;
        var needsScheduleUpgrade = cycle is { Status: "Active", Schedule: null };
        if (cycle is not null &&
            cycle.CycleId == thisMonday.ToString("yyyy-MM-dd") &&
            (needsReminderPolicyUpgrade || needsScheduleUpgrade))
        {
            var wasLegacyDeadlineCancellation =
                needsReminderPolicyUpgrade && cycle.Status == "Cancelled" && !EveryoneVoted(cycle);
            cycle = cycle with
            {
                DeadlineUtc = WeeklyDeadlineUtc(thisMonday),
                Status = wasLegacyDeadlineCancellation ? "Active" : cycle.Status,
                ResolvedUtc = wasLegacyDeadlineCancellation ? null : cycle.ResolvedUtc,
                Schedule = cycle.Schedule ?? NewSchedule(),
                FlyerReminderCounts = cycle.FlyerReminderCounts ??
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
            SaveCycle(cycle);
            Log.Information(
                "[DateNight] Cycle {CycleId} upgraded to the current Sunday/reminder/scheduling policy{Reopened}",
                cycle.CycleId,
                wasLegacyDeadlineCancellation ? " and reopened" : "");
        }

        if (cycle is null || cycle.CycleId != thisMonday.ToString("yyyy-MM-dd"))
        {
            // A confirmed showtime can legitimately fall just across the Monday
            // boundary. Keep it until the whole showing lifecycle has concluded:
            // the one-hour start window when unstarted, or four hours from
            // showtime after playback begins.
            if (cycle?.Schedule is { Status: "Locked", LockedSlot: not null })
                return cycle;

            cycle = await IssueAsync(thisMonday, ct);
            if (cycle is null) return null;
        }

        if (cycle.Status == "Active" && now.UtcDateTime > cycle.DeadlineUtc)
        {
            cycle = Resolve(cycle);
            SaveCycle(cycle);
        }

        return cycle;
    }

    /// <summary>The dry run's cycle — no real weekly timing at all, since it's
    /// purely admin-driven. Lazily draws one the first time it's needed (mirrors
    /// AdvanceAsync's "issue if none exists" behavior, minus the calendar/skip/deadline
    /// machinery, which doesn't apply to a test). Like the real cycle, the last
    /// required vote resolves it automatically.</summary>
    public async Task<WeeklyCycle> GetCurrentTestCycleAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = LoadCycle(isTest: true);
            if (cycle is not null) return await AdvanceShowtimeLifecycleAsync(cycle, isTest: true, ct);
            return await IssueTestCycleAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Read-only look at the test cycle for the admin panel — deliberately
    /// does *not* lazily draw one. Auto-issuing belongs to the real /date-night page
    /// (where an admin has actively started impersonating and wants to be dropped
    /// straight into a dry run); simply opening the admin pool page to check status
    /// should never silently spawn one and sacrifice a pool movie.</summary>
    public WeeklyCycle? PeekTestCycle() => LoadCycle(isTest: true);

    /// <summary>Shared by every mutating dry-run/real method: resolves which cycle to
    /// operate on so callers don't each re-implement "test uses the lazy test cycle,
    /// real uses the lazy real one." Must be called with <see cref="_lock"/> already
    /// held, same as the <see cref="AdvanceAsync"/> calls it replaces.</summary>
    private async Task<WeeklyCycle?> GetCycleForMutationAsync(bool isTest, CancellationToken ct)
    {
        if (!isTest) return await AdvanceAsync(ct);

        var cycle = LoadCycle(isTest: true);
        return cycle ?? await IssueTestCycleAsync(ct);
    }

    /// <summary>Draws a fresh test cycle. Caller must hold <see cref="_lock"/>. Reuses
    /// the real eligibility rules (obtainable, not really watched/never-show/cooling)
    /// plus the test lists on top, so a dry run can't resurface a genuinely retired
    /// movie, and repeat test rounds don't keep reselecting one just tested down.</summary>
    private async Task<WeeklyCycle> IssueTestCycleAsync(CancellationToken ct)
    {
        var (draw, _) = await DrawPreparedMoviesAsync(isTest: true, ct);
        if (draw.Count == 0)
            throw new InvalidOperationException(
                "No eligible movies for a dry run — every obtainable pool movie is real-watched/" +
                "never-show/cooling-off or already test-watched/never-show/cooling-off. Try Reset dry run.");

        var cycle = new WeeklyCycle(
            TestCycleId, draw, DateTime.UtcNow, DateTime.UtcNow.AddYears(10), "Active",
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            null, null, NewSchedule());

        SaveCycle(cycle, isTest: true);
        Log.Information("[DateNight] Test cycle drawn — {Count} movies (dry run, no real Mom/Dad impact)", draw.Count);
        WarmSummaryReserve(isTest: true);
        return cycle;
    }

    /// <summary>Draws this week's movies from the eligible pool and opens voting.
    /// Returns null (issuing nothing) only if zero movies are currently eligible —
    /// not reachable at the pool's current size, but a real possibility once the pool
    /// has aged for years, and issuing an empty cycle would be worse than skipping a week.</summary>
    private async Task<WeeklyCycle?> IssueAsync(DateOnly monday, CancellationToken ct)
    {
        var (draw, _) = await DrawPreparedMoviesAsync(isTest: false, ct);
        if (draw.Count == 0)
        {
            Log.Warning("[DateNight] No eligible movies — cycle for week of {Monday} not issued", monday);
            return null;
        }
        var deadline = WeeklyDeadlineUtc(monday);

        var cycle = new WeeklyCycle(
            monday.ToString("yyyy-MM-dd"), draw, DateTime.UtcNow, deadline, "Active",
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            null, null, NewSchedule(),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        SaveCycle(cycle);
        Log.Information("[DateNight] Cycle issued for week of {Monday} — {Count} movies drawn", monday, draw.Count);
        WarmSummaryReserve(isTest: false);
        return cycle;
    }

    /// <summary>Selects the five movies persisted in the prepared reserve. The
    /// reserve itself was randomly sampled from the full eligible pool after the
    /// previous issuance (or by the startup warmer), so keeping reads instant does
    /// not quietly restrict every future draw to whichever summaries happened to
    /// be cached first. If startup preparation has not completed, this method fills
    /// the reserve synchronously as a safe fallback.</summary>
    private async Task<(List<int> Draw, List<JsonObject> Eligible)> DrawPreparedMoviesAsync(
        bool isTest, CancellationToken ct)
    {
        var eligible = await GetEligibleMoviesAsync(isTest, ct);
        if (eligible.Count == 0) return ([], eligible);
        if (eligible.Count < 5)
            throw new InvalidOperationException(
                $"Date Night requires 5 eligible movies, but only {eligible.Count} are currently available.");

        var eligibleById = eligible.ToDictionary(m => (int)m["id"]!);
        var cachedIds = _summaries.GetCachedMovieIds();
        var preparedIds = LoadSummaryReserve(isTest)
            .Where(id => eligibleById.ContainsKey(id) && cachedIds.Contains(id))
            .Distinct()
            .Take(5)
            .ToList();

        if (preparedIds.Count < 5)
        {
            var toPrepare = eligible
                .Where(m => !preparedIds.Contains((int)m["id"]!))
                .OrderBy(_ => Random.Shared.Next())
                .Take(5 - preparedIds.Count)
                .ToList();
            await _summaries.EnsureSummariesAsync(toPrepare.Select(SummaryCandidate), ct);
            cachedIds = _summaries.GetCachedMovieIds();
            preparedIds.AddRange(toPrepare
                .Select(m => (int)m["id"]!)
                .Where(cachedIds.Contains));
        }

        if (preparedIds.Count < 5)
            throw new InvalidOperationException(
                "Could not prepare summaries for all 5 Date Night movies. Please try again.");

        var draw = preparedIds.Take(5).ToList();
        SaveSummaryReserve([], isTest); // consumed; the warmer builds the next five
        return (draw, eligible);
    }

    /// <summary>Builds and persists the next unbiased five-movie draw ahead of
    /// time. Called at server startup and after every real/test issuance. Holding
    /// the cycle lock prevents a Monday request from consuming a half-built reserve.</summary>
    public async Task PrepareNextDrawAsync(bool isTest = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var eligible = await GetEligibleMoviesAsync(isTest, ct);
            var current = LoadCycle(isTest)?.MovieIds.ToHashSet() ?? [];
            var eligibleIds = eligible.Select(m => (int)m["id"]!).ToHashSet();
            var cachedIds = _summaries.GetCachedMovieIds();
            var existing = LoadSummaryReserve(isTest)
                .Where(id => eligibleIds.Contains(id) && !current.Contains(id) && cachedIds.Contains(id))
                .Distinct()
                .Take(5)
                .ToList();
            if (existing.Count == 5) return;

            var candidates = eligible
                .Where(m => !current.Contains((int)m["id"]!))
                .OrderBy(_ => Random.Shared.Next())
                .Take(5)
                .ToList();
            if (candidates.Count < 5) return;

            await _summaries.EnsureSummariesAsync(candidates.Select(SummaryCandidate), ct);
            cachedIds = _summaries.GetCachedMovieIds();
            var reserve = candidates.Select(m => (int)m["id"]!).Where(cachedIds.Contains).ToList();
            if (reserve.Count == 5)
            {
                SaveSummaryReserve(reserve, isTest);
                Log.Information("[DateNight] Prepared the next 5-movie summary reserve{Test}", isTest ? " (dry run)" : "");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void WarmSummaryReserve(bool isTest)
    {
        // Re-read eligibility under the lock so a concurrent vote/restore cannot
        // leave the persisted reserve stale.
        _ = Task.Run(async () =>
        {
            try { await PrepareNextDrawAsync(isTest); }
            catch (Exception ex)
            {
                Log.Warning("[DateNight] Background summary preparation failed: {Message}", ex.Message);
            }
        });
    }

    private List<int> LoadSummaryReserve(bool isTest) =>
        JsonSerializer.Deserialize<List<int>>(
            _db.GetState(isTest ? TestSummaryReserveStateKey : SummaryReserveStateKey) ?? "[]", JsonOptions) ?? [];

    private void SaveSummaryReserve(List<int> movieIds, bool isTest) =>
        _db.SetState(isTest ? TestSummaryReserveStateKey : SummaryReserveStateKey,
            JsonSerializer.Serialize(movieIds, JsonOptions));

    private static (int MovieId, string Title, int? Year, string? Overview) SummaryCandidate(JsonObject movie) =>
        ((int)movie["id"]!,
         movie["title"]?.ToString() ?? $"#{(int)movie["id"]!}",
         (int?)movie["year"],
         movie["overview"]?.ToString());

    /// <summary>Pool movies (from Radarr, via the availability service) eligible for a
    /// fresh draw: obtainable (already on disk, or verified available), not permanently
    /// excluded (watched / never-show), and not currently in a disagreement cooling-off.
    /// Real exclusions always apply, even for a test draw — a dry run must never
    /// resurface a movie that's genuinely retired. When <paramref name="isTest"/>, the
    /// separate test lists are checked too, so repeat dry runs don't keep reselecting
    /// something just tested down.</summary>
    private async Task<List<JsonObject>> GetEligibleMoviesAsync(bool isTest, CancellationToken ct)
    {
        var pool = await _availability.GetPoolMoviesAsync(ct);
        var availability = _availability.GetAvailability();
        var lists = GetLists();
        var testLists = isTest ? GetLists(isTest: true) : null;
        var coolingOffCutoff = DateTime.UtcNow - CoolingOff;

        return pool.OfType<JsonObject>()
            .Where(m => (int?)m["id"] is int && IsEligible(m))
            .ToList();

        bool IsEligible(JsonObject movie)
        {
            var id = (int)movie["id"]!;
            var hasFile = (bool?)movie["hasFile"] ?? false;
            var isAvailable = availability.TryGetValue(id, out var a) && a.IsAvailable;
            if (!hasFile && !isAvailable) return false;

            if (!IsListEligible(lists)) return false;
            if (testLists is not null && !IsListEligible(testLists)) return false;
            return true;

            bool IsListEligible(Dictionary<int, MovieListEntry> checkLists)
            {
                if (!checkLists.TryGetValue(id, out var entry)) return true;
                if (entry.NeverShowAgain) return false;
                if (entry.Watched) return false;
                if (entry.LastDisagreedUtc is DateTime disagreed && disagreed > coolingOffCutoff) return false;
                return true;
            }
        }
    }

    /// <summary>Resolves a cycle: both people must have voted on every drawn movie to
    /// resolve at all; among mutual thumbs-ups, one is picked at random. Called the instant
    /// the last vote comes in (see <see cref="CastVoteAsync"/>) — the Sunday deadline
    /// (<see cref="AdvanceAsync"/>) only ever reaches this for a cycle voting never finished,
    /// which is why "everyone voted" is still checked here rather than assumed. Kept distinct
    /// from "cancelled" (someone didn't finish voting) because they mean different things to
    /// an admin reading the panel, even though both are "no date night this week" to Mom and Dad.</summary>
    private WeeklyCycle Resolve(WeeklyCycle cycle, bool isTest = false)
    {
        if (!EveryoneVoted(cycle))
        {
            Log.Information("[DateNight] Cycle {CycleId} cancelled — voting incomplete by the deadline", cycle.CycleId);
            return cycle with { Status = "Cancelled", ResolvedUtc = DateTime.UtcNow };
        }

        var mutual = cycle.MovieIds
            .Where(id => HouseholdOwners.Names.Where(IsAudience)
                .All(p => cycle.Votes.TryGetValue(p, out var v) && v.TryGetValue(id, out var vote) && vote == "Up"))
            .ToList();

        if (mutual.Count == 0)
        {
            Log.Information("[DateNight] Cycle {CycleId} — everyone voted, no mutual approval", cycle.CycleId);
            return cycle with { Status = "NoMatch", ResolvedUtc = DateTime.UtcNow };
        }

        var picked = mutual[Random.Shared.Next(mutual.Count)];

        var lists = GetLists(isTest);
        foreach (var id in mutual.Where(id => id != picked))
        {
            var entry = lists.TryGetValue(id, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
            lists[id] = entry with { LastApprovedUtc = DateTime.UtcNow };
        }
        SaveLists(lists, isTest);

        Log.Information("[DateNight] Cycle {CycleId} resolved — movie {Picked} picked from {Count} mutual approvals",
            cycle.CycleId, picked, mutual.Count);
        return cycle with
        {
            Status = "Resolved",
            ResolvedMovieId = picked,
            ResolvedUtc = DateTime.UtcNow,
            // The first completed voter may already have proposed dates while
            // voting was still Active. Preserve that handshake as the movie
            // winner resolves instead of resetting it.
            Schedule = cycle.Schedule ?? NewSchedule()
        };
    }

    private static ScheduleState NewSchedule() =>
        new("AwaitingProposal", null, [], null, null, [], null);

    /// <summary>Whether every audience member has voted on every one of this week's drawn
    /// movies — shared by <see cref="Resolve"/> (to decide Resolved vs. Cancelled) and
    /// <see cref="CastVoteAsync"/> (to decide whether a just-cast vote was the last one
    /// needed to resolve immediately).</summary>
    private static bool EveryoneVoted(WeeklyCycle cycle) =>
        HouseholdOwners.Names
            .Where(IsAudience)
            .All(p => cycle.Votes.TryGetValue(p, out var votes)
                      && cycle.MovieIds.All(votes.ContainsKey));

    private static bool IsAudience(string person) =>
        HouseholdOwners.Names.Any(n =>
            !string.Equals(n, "Paul", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(n, person, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records one person's vote on one of this week's drawn movies. Side effects
    /// on the permanent lists fire immediately — not deferred to resolution — since a
    /// never-show or disagreement is true regardless of how the rest of the week plays out.</summary>
    public async Task<WeeklyCycle> CastVoteAsync(string person, int movieId, string vote, bool isTest = false, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad vote.");
        if (!ValidVotes.Contains(vote))
            throw new ArgumentException($"Unrecognized vote '{vote}'.", nameof(vote));

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await GetCycleForMutationAsync(isTest, ct)
                ?? throw new InvalidOperationException("No active cycle — this week is skipped or none has been issued.");
            if (cycle.Status != "Active")
                throw new InvalidOperationException($"This week's cycle is already {cycle.Status}.");
            if (!cycle.MovieIds.Contains(movieId))
                throw new InvalidOperationException("That movie isn't one of this week's drawn movies.");

            var votes = cycle.Votes.TryGetValue(person, out var existing)
                ? new Dictionary<int, string>(existing)
                : new Dictionary<int, string>();
            votes[movieId] = vote;

            var newVotes = new Dictionary<string, Dictionary<int, string>>(cycle.Votes, StringComparer.OrdinalIgnoreCase)
            {
                [person] = votes
            };
            cycle = cycle with { Votes = newVotes };
            SaveCycle(cycle, isTest);

            // Reconcile exclusion state from the current ballots rather than only
            // ever setting flags. This lets someone correct an accidental Down or
            // Never tap before resolution without leaving the movie permanently
            // excluded after their visible vote has changed.
            var movieVotes = newVotes.Values
                .Select(v => v.TryGetValue(movieId, out var currentVote) ? currentVote : null)
                .Where(v => v is not null)
                .ToList();
            var hasNever = movieVotes.Any(v => v!.Equals("Never", StringComparison.OrdinalIgnoreCase));
            var hasDown = movieVotes.Any(v => v!.Equals("Down", StringComparison.OrdinalIgnoreCase));
            if (hasNever || hasDown || GetLists(isTest).ContainsKey(movieId))
            {
                var lists = GetLists(isTest);
                var entry = lists.TryGetValue(movieId, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
                entry = entry with
                {
                    NeverShowAgain = hasNever,
                    NeverShowAgainUtc = hasNever ? entry.NeverShowAgainUtc ?? DateTime.UtcNow : null,
                    LastDisagreedUtc = hasDown ? entry.LastDisagreedUtc ?? DateTime.UtcNow : null
                };
                lists[movieId] = entry;
                SaveLists(lists, isTest);
            }

            Log.Information("[DateNight] {Person} voted {Vote} on movie {MovieId}{Test}", person, vote, movieId, isTest ? " (dry run)" : "");

            // Resolve the instant the last vote comes in, rather than waiting for
            // Sunday — the deadline (AdvanceAsync) only ever needs to run Resolve
            // itself for a cycle where voting never finished.
            if (EveryoneVoted(cycle))
            {
                cycle = Resolve(cycle, isTest);
                SaveCycle(cycle, isTest);
            }

            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether the flyer is owed to this person today — active week, they
    /// still owe either movie votes or their initial time proposal, it has not already
    /// been shown today (Hawaii date), and they have received fewer than three daily
    /// prompts. The flyer remains manually reachable after the gentle prompts stop.</summary>
    public static bool IsFlyerOwedToday(string person, WeeklyCycle cycle)
    {
        if (cycle.Status != "Active") return false;

        var votes = cycle.Votes.TryGetValue(person, out var v) ? v : null;
        var ballotComplete = votes is not null && cycle.MovieIds.All(votes.ContainsKey);
        var stillNeedsInitialTimes = ballotComplete && cycle.Schedule?.Status == "AwaitingProposal";
        if (ballotComplete && !stillNeedsInitialTimes) return false;

        var reminderCount = cycle.FlyerReminderCounts is not null &&
                            cycle.FlyerReminderCounts.TryGetValue(person, out var count)
            ? count
            : 0;
        if (reminderCount >= MaxFlyerReminderCount) return false;

        if (!cycle.LastFlyerShownUtc.TryGetValue(person, out var last)) return true;
        return HawaiiDate(last) != HawaiiDate(DateTime.UtcNow);
    }

    /// <summary>The responder to a schedule proposal gets one gentle prompt per
    /// Hawaii day until they approve, counter, or cancel. Proposal changes replace
    /// the reminder map, so the new state can surface immediately.</summary>
    public bool IsScheduleReminderOwedToday(string person, WeeklyCycle cycle)
    {
        var schedule = cycle.Schedule;
        if (cycle.Status != "Resolved" || schedule?.Status != "AwaitingApproval") return false;
        if (string.Equals(schedule.ProposedBy, person, StringComparison.OrdinalIgnoreCase)) return false;

        if (schedule.LastReminderShownUtc is null ||
            !schedule.LastReminderShownUtc.TryGetValue(person, out var last))
            return true;
        return HawaiiDate(last) != HawaiiDate(DateTime.UtcNow);
    }

    private static DateOnly HawaiiDate(DateTime utc) =>
        DateOnly.FromDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(HawaiiOffset).Date);

    public async Task RecordFlyerShownAsync(string person, bool isTest = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await GetCycleForMutationAsync(isTest, ct);
            if (cycle is null) return;

            var now = DateTime.UtcNow;
            var alreadyShownToday = cycle.LastFlyerShownUtc.TryGetValue(person, out var last) &&
                                    HawaiiDate(last) == HawaiiDate(now);
            var shown = new Dictionary<string, DateTime>(cycle.LastFlyerShownUtc, StringComparer.OrdinalIgnoreCase)
            {
                [person] = now
            };
            var reminderCounts = cycle.FlyerReminderCounts is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(cycle.FlyerReminderCounts, StringComparer.OrdinalIgnoreCase);
            if (!alreadyShownToday)
            {
                reminderCounts.TryGetValue(person, out var previousCount);
                reminderCounts[person] = Math.Min(MaxFlyerReminderCount, previousCount + 1);
            }

            SaveCycle(cycle with
            {
                LastFlyerShownUtc = shown,
                FlyerReminderCounts = reminderCounts
            }, isTest);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Earliest allowed slot time — "after 12pm" per spec.</summary>
    private static readonly TimeOnly EarliestSlotTime = new(12, 0);

    /// <summary>Latest allowed slot time — "before 12am" per spec, on the same
    /// 30-minute grid as everything else.</summary>
    private static readonly TimeOnly LatestSlotTime = new(23, 30);

    /// <summary>Validates a proposed slot list against the shape the scheduling form
    /// is supposed to produce — enforced here too, not just in the UI, so a stray API
    /// call can't exceed it. Initial proposals may contain combinations across the
    /// displayed dates; every time is on a 30-minute boundary within
    /// [noon, 11:30pm] and every resulting slot must still be in the future.</summary>
    private static void ValidateSlots(List<ProposedSlot> slots)
    {
        if (slots is not { Count: > 0 })
            throw new ArgumentException("At least one slot must be proposed.", nameof(slots));

        var distinctDays = slots.Select(s => s.Date).Distinct().Count();
        if (distinctDays > 7)
            throw new ArgumentException("At most 7 days may be proposed.", nameof(slots));

        foreach (var slot in slots)
        {
            if (!DateOnly.TryParse(slot.Date, out var date))
                throw new ArgumentException($"Invalid date '{slot.Date}'.", nameof(slots));
            if (!TimeOnly.TryParse(slot.Time, out var time) || time.Minute % 30 != 0)
                throw new ArgumentException($"Invalid time '{slot.Time}' — must be on a 30-minute boundary.", nameof(slots));
            if (time < EarliestSlotTime || time > LatestSlotTime)
                throw new ArgumentException($"Time '{slot.Time}' is outside the noon–11:30pm window.", nameof(slots));
            if (ToUtc(date, time) <= DateTime.UtcNow)
                throw new ArgumentException($"Slot '{slot.Date} {slot.Time}' must be in the future.", nameof(slots));
        }
    }

    /// <summary>A real cycle owns exactly one Hawaii calendar week, Monday through
    /// Sunday. Keeping every proposal inside that range prevents an old week's
    /// negotiation from creating a second Date Night in the following week. The
    /// isolated dry run has no calendar week and intentionally keeps its rolling
    /// seven-day test window.</summary>
    private static void ValidateSlotsForCycle(WeeklyCycle cycle, List<ProposedSlot> slots, bool isTest)
    {
        if (isTest) return;
        if (!DateOnly.TryParse(cycle.CycleId, out var monday))
            throw new InvalidOperationException("This Date Night cycle has an invalid calendar week.");

        var sunday = monday.AddDays(6);
        foreach (var slot in slots)
        {
            var date = DateOnly.Parse(slot.Date);
            if (date < monday || date > sunday)
                throw new ArgumentException(
                    $"Slot '{slot.Date} {slot.Time}' must fall within this Date Night week ({monday:MMM d}–{sunday:MMM d}).",
                    nameof(slots));
        }
    }

    /// <summary>The schedule handshake's propose step — used both for the first
    /// proposal (either person, whoever gets there first) and for a counter-proposal
    /// (only whoever's turn it is to respond may counter; the current proposer just
    /// has to wait). Counter-proposing can ping-pong indefinitely — only Approve or
    /// Cancel end it.</summary>
    public async Task<WeeklyCycle> ProposeScheduleAsync(string person, List<ProposedSlot> slots, bool isTest = false, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");
        ValidateSlots(slots);

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await GetCycleForMutationAsync(isTest, ct)
                ?? throw new InvalidOperationException("No active week to schedule.");
            ValidateSlotsForCycle(cycle, slots, isTest);
            if (cycle.Status is not ("Active" or "Resolved"))
                throw new InvalidOperationException("This week's date night is no longer accepting proposals.");

            var schedule = cycle.Schedule ?? NewSchedule();
            var isFirstProposal = schedule.Status == "AwaitingProposal";
            var isCounterProposal = schedule.Status == "AwaitingApproval" &&
                !string.Equals(schedule.ProposedBy, person, StringComparison.OrdinalIgnoreCase);
            if (!isFirstProposal && !isCounterProposal)
                throw new InvalidOperationException("It's not your turn to propose right now.");
            if (isFirstProposal &&
                (!cycle.Votes.TryGetValue(person, out var ballot) ||
                 !cycle.MovieIds.All(ballot.ContainsKey)))
                throw new InvalidOperationException(
                    "Finish voting on all of this week's movies before proposing times.");
            if (isCounterProposal && slots.Count != 1)
                throw new ArgumentException(
                    "A counter-proposal must name exactly one replacement date and time.",
                    nameof(slots));

            cycle = cycle with
            {
                Schedule = schedule with
                {
                    Status = "AwaitingApproval",
                    ProposedBy = person,
                    ProposedSlots = slots,
                    AcknowledgedBy = [person],
                    LastReminderShownUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                }
            };
            SaveCycle(cycle, isTest);
            Log.Information("[DateNight] {Person} {Verb} {Count} slot(s) for this week's date night{Test}",
                person, isCounterProposal ? "counter-proposed" : "proposed", slots.Count, isTest ? " (dry run)" : "");
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Second step — the *other* person (not the proposer) picks exactly one
    /// of the offered slots. Locking in is what triggers the actual download; nothing
    /// downloads before this point, per spec.</summary>
    public async Task<WeeklyCycle> ApproveScheduleAsync(string person, ProposedSlot slot, bool isTest = false, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");

        WeeklyCycle cycle;
        await _lock.WaitAsync(ct);
        try
        {
            cycle = await GetCycleForMutationAsync(isTest, ct)
                ?? throw new InvalidOperationException("No active week to schedule.");
            if (cycle.Status != "Resolved" || cycle.ResolvedMovieId is null)
                throw new InvalidOperationException(
                    "Both movie ballots must be complete before a showtime can be confirmed.");
            // A browser retry/double tap can arrive after the first request has
            // persisted the lock but while that request is still waiting for
            // Radarr's interactive search. Treat confirmation of the exact same
            // slot as success instead of returning a misleading 409; importantly,
            // return here so the download is not triggered twice.
            if (cycle.Schedule?.Status == "Locked" &&
                cycle.Schedule.LockedSlot is { } locked &&
                locked.Date == slot.Date && locked.Time == slot.Time)
                return cycle;
            if (cycle.Schedule?.Status != "AwaitingApproval")
                throw new InvalidOperationException("There's no proposal waiting on approval.");
            if (string.Equals(cycle.Schedule.ProposedBy, person, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The other person needs to approve — you proposed this one.");
            if (!cycle.Schedule.ProposedSlots.Any(s => s.Date == slot.Date && s.Time == slot.Time))
                throw new InvalidOperationException("That slot wasn't one of the proposed options.");
            if (ParseSlotToUtc(slot) <= DateTime.UtcNow)
                throw new InvalidOperationException("That showtime has already passed — choose another proposed time or send a counter-offer.");

            cycle = cycle with
            {
                Schedule = cycle.Schedule with
                {
                    Status = "Locked",
                    LockedSlot = slot,
                    LockedUtc = DateTime.UtcNow,
                    DownloadStatus = "Searching",
                    DownloadMessage = null,
                    DownloadUpdatedUtc = DateTime.UtcNow
                }
            };
            SaveCycle(cycle, isTest);
            Log.Information("[DateNight] {Person} approved {Date} {Time} — locking in movie {MovieId}{Test}",
                person, slot.Date, slot.Time, cycle.ResolvedMovieId, isTest ? " (dry run)" : "");
        }
        finally
        {
            _lock.Release();
        }

        // Outside the lock — this is a slow network round-trip to Radarr, and a
        // hiccup here shouldn't undo the lock-in Mom and Dad just agreed to. Always
        // real, dry run included — a real grab is the whole point of testing this.
        if (cycle.ResolvedMovieId is not int movieId)
            return cycle;

        var result = await TriggerDownloadAsync(movieId, ct);
        return await SaveDownloadResultAsync(cycle.CycleId, movieId, result, isTest, ct);
    }

    /// <summary>Grabs the best obtainable release for a just-locked movie, reusing the
    /// exact same "would we actually accept this" judgment the availability scan uses
    /// (<see cref="DateNightAvailabilityService.IsObtainable"/>) rather than re-deriving
    /// it. Best-effort: setting the movie monitored means Radarr will keep trying on its
    /// own schedule even if this immediate grab attempt finds nothing or fails outright.</summary>
    private sealed record DownloadAttempt(string Status, string Message);

    private async Task<DownloadAttempt> TriggerDownloadAsync(int movieId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
            await radarr.EditMoviesAsync([movieId], monitored: true, ct: ct);

            var releases = await radarr.SearchReleasesAsync(movieId, ct);
            var best = releases.OfType<JsonObject>().FirstOrDefault(DateNightAvailabilityService.IsObtainable);
            if (best is not null)
            {
                await radarr.GrabReleaseAsync(best, ct);
                return new DownloadAttempt(
                    "Requested",
                    $"Radarr accepted {best["title"]?.ToString() ?? "a release"}.");
            }

            const string noRelease = "No obtainable release was returned; Radarr is monitoring the movie and will keep trying.";
            Log.Warning("[DateNight] Lock-in for movie {MovieId}: {Message}", movieId, noRelease);
            return new DownloadAttempt("Monitoring", noRelease);
        }
        catch (Exception ex)
        {
            Log.Warning("[DateNight] Triggering download for movie {MovieId} failed: {Message}", movieId, ex.Message);
            return new DownloadAttempt("Failed", ex.Message);
        }
    }

    private async Task<WeeklyCycle> SaveDownloadResultAsync(
        string cycleId, int movieId, DownloadAttempt result, bool isTest, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var latest = LoadCycle(isTest);
            if (latest?.CycleId != cycleId ||
                latest.ResolvedMovieId != movieId ||
                latest.Schedule?.Status != "Locked")
                return latest ?? throw new InvalidOperationException("The locked cycle disappeared.");

            latest = latest with
            {
                Schedule = latest.Schedule with
                {
                    DownloadStatus = result.Status,
                    DownloadMessage = result.Message,
                    DownloadUpdatedUtc = DateTime.UtcNow
                }
            };
            SaveCycle(latest, isTest);
            return latest;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WeeklyCycle> RetryDownloadAsync(bool isTest = false, CancellationToken ct = default)
    {
        WeeklyCycle cycle;
        int movieId;
        await _lock.WaitAsync(ct);
        try
        {
            cycle = isTest
                ? LoadCycle(isTest: true) ?? throw new InvalidOperationException("No test cycle exists.")
                : await AdvanceAsync(ct) ?? throw new InvalidOperationException("No active cycle exists.");
            if (cycle.Status != "Resolved" ||
                cycle.ResolvedMovieId is not int resolvedMovieId ||
                cycle.Schedule?.Status != "Locked")
                throw new InvalidOperationException("There is no locked movie download to retry.");

            movieId = resolvedMovieId;
            cycle = cycle with
            {
                Schedule = cycle.Schedule with
                {
                    DownloadStatus = "Searching",
                    DownloadMessage = null,
                    DownloadUpdatedUtc = DateTime.UtcNow
                }
            };
            SaveCycle(cycle, isTest);
        }
        finally
        {
            _lock.Release();
        }

        var result = await TriggerDownloadAsync(movieId, ct);
        return await SaveDownloadResultAsync(cycle.CycleId, movieId, result, isTest, ct);
    }

    /// <summary>Either person can cancel — before lock-in (undoes a pending proposal)
    /// or after (also unmonitors the movie again, since a cancelled date night
    /// shouldn't leave a rogue download running against the pool's late-download design).</summary>
    public async Task<WeeklyCycle> CancelScheduleAsync(string person, bool isTest = false, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");

        WeeklyCycle cycle;
        await _lock.WaitAsync(ct);
        try
        {
            cycle = await GetCycleForMutationAsync(isTest, ct)
                ?? throw new InvalidOperationException("No active week to cancel.");
            if (cycle.Schedule is null || cycle.Schedule.Status is "Cancelled" or "AwaitingProposal")
                throw new InvalidOperationException("There's nothing scheduled to cancel.");
            if (cycle.Schedule.PlaybackStartedUtc is not null)
                throw new InvalidOperationException("Playback has already started. Use Finished watching when the movie is over.");

            var wasLocked = cycle.Schedule.Status == "Locked";
            if (wasLocked && cycle.ResolvedMovieId is int movieId)
            {
                // A cancellation is not complete until its external Radarr state
                // agrees. If this call fails, leave the schedule Locked and return
                // an error so the user can retry; never persist a successful-looking
                // cancellation that silently leaves the movie monitored.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
                    await radarr.CancelMovieDownloadAsync(movieId, ct);
                    await radarr.EditMoviesAsync([movieId], monitored: false, ct: ct);
                }
                catch (Exception ex)
                {
                    Log.Warning("[DateNight] Could not unmonitor movie {MovieId} while cancelling: {Message}", movieId, ex.Message);
                    throw new InvalidOperationException("Radarr could not cancel the movie download. Please try again.", ex);
                }
            }

            cycle = cycle with
            {
                Schedule = cycle.Schedule with { Status = "Cancelled", CancelledBy = person, AcknowledgedBy = [person] }
            };
            SaveCycle(cycle, isTest);
            Log.Information("[DateNight] {Person} cancelled this week's date night{Test}", person, isTest ? " (dry run)" : "");
        }
        finally
        {
            _lock.Release();
        }

        return cycle;
    }

    /// <summary>Marks that this person has seen the schedule's current state (a
    /// proposal waiting on them, or a cancellation) — idempotent, same shape as
    /// <see cref="DateNightAvailabilityService.MarkAnnouncementSeen"/>. Drives whether
    /// the frontend pops the "your turn" / "cancelled" modal again on the next load.</summary>
    public async Task<WeeklyCycle> AcknowledgeScheduleAsync(string person, bool isTest = false, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await GetCycleForMutationAsync(isTest, ct)
                ?? throw new InvalidOperationException("No active week.");
            if (cycle.Schedule is null)
                return cycle;

            var acknowledged = new List<string>(cycle.Schedule.AcknowledgedBy);
            if (!acknowledged.Contains(person, StringComparer.OrdinalIgnoreCase))
                acknowledged.Add(person);
            var reminded = cycle.Schedule.LastReminderShownUtc is null
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTime>(cycle.Schedule.LastReminderShownUtc, StringComparer.OrdinalIgnoreCase);
            reminded[person] = DateTime.UtcNow;
            cycle = cycle with
            {
                Schedule = cycle.Schedule with
                {
                    AcknowledgedBy = acknowledged,
                    LastReminderShownUtc = reminded
                }
            };
            SaveCycle(cycle, isTest);
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static DateTime ParseSlotToUtc(ProposedSlot slot) =>
        ToUtc(DateOnly.Parse(slot.Date), TimeOnly.Parse(slot.Time));

    /// <summary>Polled every 30-60s app-wide. The popup is eligible from ten
    /// minutes before showtime through the one-hour grace window, unless playback
    /// has already begun. This same poll lazily advances missed and finished
    /// showtimes into their persisted closing state.</summary>
    public async Task<ShowtimeStatus> GetShowtimeStatusAsync(bool isTest = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = LoadCycle(isTest);
            if (cycle is null) return new ShowtimeStatus(false, null, null);
            cycle = await AdvanceShowtimeLifecycleAsync(cycle, isTest, ct);

            if (cycle.Schedule?.Status != "Locked" ||
                cycle.Schedule.LockedSlot is null ||
                cycle.Schedule.PlaybackStartedUtc is not null ||
                cycle.ResolvedMovieId is null)
                return new ShowtimeStatus(false, null, null);

            var showtimeUtc = ParseSlotToUtc(cycle.Schedule.LockedSlot);
            var now = DateTime.UtcNow;
            var imminent = now >= showtimeUtc.AddMinutes(-10) && now <= showtimeUtc.AddHours(1);
            return new ShowtimeStatus(imminent, cycle.ResolvedMovieId, showtimeUtc);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Background-safe entry point that advances only an already-existing
    /// real showtime. It deliberately does not call <see cref="AdvanceAsync"/>, so
    /// the timer cannot issue a weekly draw or touch the isolated dry run.</summary>
    public async Task AdvanceRealShowtimeLifecycleAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = LoadCycle();
            if (cycle is not null)
                await AdvanceShowtimeLifecycleAsync(cycle, isTest: false, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Records the Play click before handing off to Jellyfin. Playback
    /// cannot begin early or after the one-hour grace period; repeated clicks
    /// within the window are idempotent so a transient player error can be retried.</summary>
    public async Task<WeeklyCycle> StartShowtimeAsync(bool isTest = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = isTest
                ? LoadCycle(isTest: true) ?? throw new InvalidOperationException("No test cycle exists.")
                : await AdvanceAsync(ct) ?? throw new InvalidOperationException("No active cycle exists.");
            cycle = await AdvanceShowtimeLifecycleAsync(cycle, isTest, ct);

            if (cycle.Schedule?.Status != "Locked" ||
                cycle.Schedule.LockedSlot is null ||
                cycle.ResolvedMovieId is null)
                throw new InvalidOperationException("There is no playable date night.");

            var showtimeUtc = ParseSlotToUtc(cycle.Schedule.LockedSlot);
            var now = DateTime.UtcNow;
            if (now < showtimeUtc)
                throw new InvalidOperationException("The movie cannot start before the scheduled time.");
            if (now > showtimeUtc.AddHours(1))
                throw new InvalidOperationException("The one-hour start window has passed.");

            if (cycle.Schedule.PlaybackStartedUtc is null)
            {
                // Do not record a start merely because the clock reached showtime.
                // Radarr may still be importing the file, and recording a false
                // start would incorrectly switch the lifecycle from the one-hour
                // missed-start rule to the four-hour playback rule.
                using var scope = _scopeFactory.CreateScope();
                var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
                var movie = await radarr.GetMovieAsync(cycle.ResolvedMovieId.Value, ct);
                if (movie?["hasFile"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("The movie is still downloading. Try Play again when it is ready.");

                cycle = cycle with
                {
                    Schedule = cycle.Schedule with { PlaybackStartedUtc = now }
                };
                SaveCycle(cycle, isTest);
                Log.Information("[DateNight] Playback started for movie {MovieId}{Test}",
                    cycle.ResolvedMovieId, isTest ? " (dry run)" : "");
            }
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Moves a locked showtime into its persisted closing state. An
    /// unstarted movie concludes one hour after showtime; a started movie concludes
    /// four hours after showtime if nobody explicitly marks it watched. Both paths
    /// unmonitor the Radarr movie, but deliberately keep its file and pool membership
    /// because only an explicit Finished Watching owns graduation/deletion.</summary>
    private async Task<WeeklyCycle> AdvanceShowtimeLifecycleAsync(
        WeeklyCycle cycle, bool isTest, CancellationToken ct)
    {
        if (cycle.Schedule?.Status != "Locked" ||
            cycle.Schedule.LockedSlot is null)
            return cycle;

        var showtimeUtc = ParseSlotToUtc(cycle.Schedule.LockedSlot);
        var now = DateTime.UtcNow;
        var reason = cycle.Schedule.PlaybackStartedUtc is null
            ? now > showtimeUtc.AddHours(1) ? "MissedStart" : null
            : now > showtimeUtc.AddHours(4) ? "PlaybackWindowEnded" : null;
        if (reason is null) return cycle;

        string? title = null;

        // Once the showing is over, Radarr must stop monitoring the selected
        // movie. Preserve the title before a later explicit Finished Watching
        // removes its pool tag and therefore removes it from normal pool reads.
        if (cycle.ResolvedMovieId is int movieId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
                var movie = await radarr.GetMovieAsync(movieId, ct);
                title = movie?["title"]?.ToString();
                await radarr.CancelMovieDownloadAsync(movieId, ct);
                await radarr.EditMoviesAsync([movieId], monitored: false, ct: ct);
                Log.Information("[DateNight] Movie {MovieId} unmonitored after showtime concluded ({Reason}){Test}",
                    movieId, reason, isTest ? " (dry run)" : "");
            }
            catch (Exception ex)
            {
                Log.Warning("[DateNight] Could not conclude showtime for movie {MovieId}: {Message}", movieId, ex.Message);
                // Leave it Locked so the next app-wide poll/page read retries
                // cleanup. Never claim the showing is over while Radarr still
                // says its movie is monitored.
                return cycle;
            }
        }

        var concluded = cycle with
        {
            Schedule = cycle.Schedule with
            {
                Status = "Concluded",
                ConcludedUtc = now,
                ConclusionReason = reason,
                ConclusionTitle = title,
                AcknowledgedBy = []
            }
        };
        SaveCycle(concluded, isTest);
        Log.Information("[DateNight] Cycle {CycleId} concluded after showtime ({Reason}){Test}",
            cycle.CycleId, reason, isTest ? " (dry run)" : "");
        return concluded;
    }

    /// <summary>The manual "done watching" confirmation: deletes the file, returns the
    /// movie to unmonitored, drops the pool tag, and marks it permanently watched. The
    /// movie record itself survives — it now shows in the regular library as a
    /// not-ready tile with a manual get button, per the spec's graduation stage.</summary>
    public async Task MarkWatchedAsync(int movieId, bool isTest = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();

        // Always real, dry run included — this is exactly the pipeline a dry run
        // exists to verify actually works (real file delete, real unmonitor/untag).
        var movie = await radarr.GetMovieAsync(movieId, ct);
        var title = movie?["title"]?.ToString();
        await radarr.CancelMovieDownloadAsync(movieId, ct);
        if ((int?)movie?["movieFile"]?["id"] is int movieFileId)
            await radarr.DeleteMovieFileAsync(movieFileId, ct);

        var poolTagId = await radarr.EnsureTagAsync(DateNight.PoolTag, ct);
        await radarr.EditMoviesAsync([movieId], monitored: false, removeTagIds: [poolTagId], ct: ct);

        var lists = GetLists(isTest);
        var entry = lists.TryGetValue(movieId, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
        lists[movieId] = entry with { Watched = true, WatchedUtc = DateTime.UtcNow };
        SaveLists(lists, isTest);

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = LoadCycle(isTest);
            if (cycle?.ResolvedMovieId == movieId && cycle.Schedule?.Status == "Locked")
            {
                cycle = cycle with
                {
                    Schedule = cycle.Schedule with
                    {
                        Status = "Concluded",
                        ConcludedUtc = DateTime.UtcNow,
                        ConclusionReason = "Watched",
                        ConclusionTitle = title,
                        AcknowledgedBy = []
                    }
                };
                SaveCycle(cycle, isTest);
            }
        }
        finally
        {
            _lock.Release();
        }

        Log.Information("[DateNight] Movie {MovieId} marked watched — file removed, unmonitored, pool tag removed{Test}",
            movieId, isTest ? " (dry run)" : "");
    }

    /// <summary>The leak-prevention gate: while false, Mom and Dad see only the static
    /// "coming soon" poster regardless of what's built behind it — flipped once the
    /// admin decides the feature is actually ready for them.</summary>
    public bool IsLive() => bool.TryParse(_db.GetState(LiveStateKey), out var live) && live;

    public void SetLive(bool live)
    {
        _db.SetState(LiveStateKey, live.ToString());
        Log.Information("[DateNight] Feature set {State} for Mom and Dad", live ? "LIVE" : "dark");
    }

    public Dictionary<int, MovieListEntry> GetLists(bool isTest = false)
    {
        var json = _db.GetState(isTest ? TestListsStateKey : ListsStateKey);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, MovieListEntry>>(json, JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            Log.Warning("[DateNight] Lists state unreadable, starting fresh: {Message}", ex.Message);
            return new();
        }
    }

    /// <summary>Clears whichever exclusion a movie is sitting in — never-show,
    /// cooling-off, or watched — and puts it back into rotation. Checks *both* the
    /// real and test lists and clears whichever has an entry: a movie marked watched
    /// via the dry run records that flag in the test lists, but <see
    /// cref="MarkWatchedAsync"/> always removes the real Radarr pool tag regardless
    /// of which side recorded it (Radarr has no concept of "test"), so restoring has
    /// to re-add the tag either way or a dry-run watch would have no way back. A
    /// movie only ever sits in one exclusion at a time in practice, so clearing all
    /// three on both sides is safe regardless of which is actually set. Approved
    /// history (mutually-liked-but-not-picked) is left alone; that was never an
    /// exclusion to undo.</summary>
    public async Task RestoreMovieAsync(int movieId, CancellationToken ct = default)
    {
        var lists = GetLists(isTest: false);
        var testLists = GetLists(isTest: true);
        var hasReal = lists.TryGetValue(movieId, out var entry);
        var hasTest = testLists.TryGetValue(movieId, out var testEntry);
        if (!hasReal && !hasTest) return;

        if ((hasReal && entry!.Watched) || (hasTest && testEntry!.Watched))
        {
            using var scope = _scopeFactory.CreateScope();
            var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
            var poolTagId = await radarr.EnsureTagAsync(DateNight.PoolTag, ct);
            await radarr.EditMoviesAsync([movieId], monitored: false, addTagIds: [poolTagId], ct: ct);
        }

        if (hasReal)
        {
            lists[movieId] = entry! with
            {
                NeverShowAgain = false, NeverShowAgainUtc = null,
                LastDisagreedUtc = null,
                Watched = false, WatchedUtc = null
            };
            SaveLists(lists, isTest: false);
        }
        if (hasTest)
        {
            testLists[movieId] = testEntry! with
            {
                NeverShowAgain = false, NeverShowAgainUtc = null,
                LastDisagreedUtc = null,
                Watched = false, WatchedUtc = null
            };
            SaveLists(testLists, isTest: true);
        }

        Log.Information("[DateNight] Movie {MovieId} restored to the eligible pool", movieId);
    }

    public SkipState GetSkip()
    {
        var json = _db.GetState(SkipStateKey);
        if (string.IsNullOrWhiteSpace(json)) return new SkipState(null, null, null);
        try
        {
            return JsonSerializer.Deserialize<SkipState>(json, JsonOptions) ?? new SkipState(null, null, null);
        }
        catch (JsonException)
        {
            return new SkipState(null, null, null);
        }
    }

    /// <summary>A skip by either person applies to both, per spec — one shared flag rather
    /// than tracking it per person.</summary>
    public void SetSkip(string person, string scope)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad can skip.");

        var now = NowHawaii;
        DateTime until = scope.Equals("month", StringComparison.OrdinalIgnoreCase)
            ? ToUtc(new DateOnly(now.Year, now.Month, 1).AddMonths(1), TimeOnly.MinValue)
            : ToUtc(MondayOf(now).AddDays(7), TimeOnly.MinValue);

        _db.SetState(SkipStateKey, JsonSerializer.Serialize(new SkipState(until, person, DateTime.UtcNow), JsonOptions));
        Log.Information("[DateNight] {Person} skipped this {Scope}, resuming {Until}", person, scope, until);
    }

    public void ClearSkip() =>
        _db.SetState(SkipStateKey, JsonSerializer.Serialize(new SkipState(null, null, null), JsonOptions));

    // ── Admin test helpers ──────────────────────────────────────────────────
    // These exist because the flyer/voting UI (phase 4) doesn't exist yet — the
    // admin pool page is how phase 3's state machine gets exercised end to end.

    public async Task<WeeklyCycle?> ForceIssueAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = NowHawaii;
            return await IssueAsync(MondayOf(now), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Runs the deadline-resolution logic immediately, regardless of the real
    /// deadline — lets an admin verify both the Resolved/NoMatch and Cancelled paths
    /// without waiting for the end of the week.</summary>
    public async Task<WeeklyCycle?> ResolveNowAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = LoadCycle();
            if (cycle is null || cycle.Status != "Active") return cycle;
            cycle = Resolve(cycle);
            SaveCycle(cycle);
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void DiscardCycle() => _db.SetState(CycleStateKey, "null");

    /// <summary>Wipes the dry run back to a totally clean slate — both the test cycle
    /// and every test list entry (never-show/cooldown/watched from prior test rounds).
    /// The real cycle/lists Mom and Dad actually use are never touched by this.</summary>
    public async Task<WeeklyCycle> ResetDryRunAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var oldCycle = LoadCycle(isTest: true);
            var oldLists = GetLists(isTest: true);
            var watchedIds = oldLists.Where(kv => kv.Value.Watched).Select(kv => kv.Key).ToList();
            var lockedMovieId = oldCycle?.Schedule?.Status == "Locked" ? oldCycle.ResolvedMovieId : null;

            // Dry-run bookkeeping is isolated, but its Radarr actions are real.
            // Restore every external mutation before throwing that bookkeeping
            // away, otherwise Reset could strand a watched movie without its pool
            // tag or leave an abandoned grab running in the download client.
            if (watchedIds.Count > 0 || lockedMovieId is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
                if (watchedIds.Count > 0)
                {
                    var poolTagId = await radarr.EnsureTagAsync(DateNight.PoolTag, ct);
                    await radarr.EditMoviesAsync(watchedIds, monitored: false, addTagIds: [poolTagId], ct: ct);
                }
                if (lockedMovieId is int movieId)
                {
                    await radarr.CancelMovieDownloadAsync(movieId, ct);
                    await radarr.EditMoviesAsync([movieId], monitored: false, ct: ct);
                }
            }

            _db.SetState(TestCycleStateKey, "null");
            _db.SetState(TestListsStateKey, "null");
            Log.Information("[DateNight] Dry run reset — test cycle and test lists cleared");

            // Pay any one-time summary-generation cost while the admin explicitly
            // starts over, not when Mom/Dad opens the ballot. The issued cycle and
            // all five cached pitches are ready before this request returns.
            return await IssueTestCycleAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private WeeklyCycle? LoadCycle(bool isTest = false)
    {
        var json = _db.GetState(isTest ? TestCycleStateKey : CycleStateKey);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<WeeklyCycle>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Warning("[DateNight] Cycle state unreadable, starting fresh: {Message}", ex.Message);
            return null;
        }
    }

    private void SaveCycle(WeeklyCycle cycle, bool isTest = false) =>
        _db.SetState(isTest ? TestCycleStateKey : CycleStateKey, JsonSerializer.Serialize(cycle, JsonOptions));

    private void SaveLists(Dictionary<int, MovieListEntry> lists, bool isTest = false) =>
        _db.SetState(isTest ? TestListsStateKey : ListsStateKey, JsonSerializer.Serialize(lists, JsonOptions));
}
