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
    List<int> MovieIds,                                  // 1-3, drawn at issuance
    DateTime IssuedUtc,
    DateTime DeadlineUtc,                                // Thursday 23:59:59 Hawaii, stored as UTC
    string Status,                                        // Active | Resolved | NoMatch | Cancelled
    Dictionary<string, Dictionary<int, string>> Votes,   // person -> movieId -> "Up"|"Down"|"Never"
    Dictionary<string, DateTime> LastFlyerShownUtc,      // person -> last time the flyer was shown (once/day)
    int? ResolvedMovieId,
    DateTime? ResolvedUtc,
    ScheduleState? Schedule);                             // null until Resolved

/// <summary>One proposed day/time, in Hawaii local terms — kept as plain strings
/// rather than a DateTime so "Friday at 7pm" round-trips through JSON exactly as
/// typed, with no timezone ambiguity to introduce on the way in or out.</summary>
public sealed record ProposedSlot(string Date, string Time); // "yyyy-MM-dd", "HH:mm"

/// <summary>The propose -> approve -> lock handshake for one resolved week's movie.
/// Created the moment a cycle resolves; "first person" isn't a fixed role — whoever
/// proposes first is the proposer, and the other approves or cancels.</summary>
public sealed record ScheduleState(
    string Status,               // AwaitingProposal | AwaitingApproval | Locked | Cancelled
    string? ProposedBy,
    List<ProposedSlot> ProposedSlots,
    ProposedSlot? LockedSlot,
    DateTime? LockedUtc);

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
/// Issuance and deadline resolution are both *lazy* — evaluated on whichever request
/// happens to touch the cycle next, rather than by a background timer. There is no
/// scheduled-job infrastructure elsewhere in this app (the availability scan is also
/// request-triggered), and a timer would have nothing to do that a request-time check
/// doesn't already cover: the app has no push notifications, so nobody can be told
/// about a new cycle before they open it anyway.
///
/// Week boundaries are Hawaii time, not server UTC — the server runs UTC (see
/// docker-compose.yml) but Mom and Dad experience "Monday" and "Thursday" in Hawaii.
/// Implemented as a fixed -10:00 offset rather than a named timezone lookup: Hawaii
/// never observes DST, so the fixed offset is exactly as correct and doesn't depend on
/// the container image having IANA tzdata installed.
/// </summary>
public class DateNightCycleService
{
    private const string CycleStateKey = "date-night:cycle";
    private const string ListsStateKey = "date-night:lists";
    private const string SkipStateKey = "date-night:skip";
    private const string LiveStateKey = "date-night:live";

    private static readonly TimeSpan HawaiiOffset = TimeSpan.FromHours(-10);

    /// <summary>Public so the admin endpoint can compute "still cooling off" the same
    /// way issuance does, instead of re-hardcoding the 4 weeks.</summary>
    public static readonly TimeSpan CoolingOff = TimeSpan.FromDays(28); // 4 weeks

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> ValidVotes = new(StringComparer.OrdinalIgnoreCase) { "Up", "Down", "Never" };

    private readonly DateNightAvailabilityService _availability;
    private readonly AppDatabase _db;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Guards the read-evaluate-write sequence in <see cref="GetCurrentCycleAsync"/>
    /// against a race where two near-simultaneous requests both decide a new cycle is due
    /// and each issues (and persists) its own draw.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DateNightCycleService(DateNightAvailabilityService availability, AppDatabase db, IServiceScopeFactory scopeFactory)
    {
        _availability = availability;
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

        if (cycle is null || cycle.CycleId != thisMonday.ToString("yyyy-MM-dd"))
        {
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

    /// <summary>Draws this week's movies from the eligible pool and opens voting.
    /// Returns null (issuing nothing) only if zero movies are currently eligible —
    /// not reachable at the pool's current size, but a real possibility once the pool
    /// has aged for years, and issuing an empty cycle would be worse than skipping a week.</summary>
    private async Task<WeeklyCycle?> IssueAsync(DateOnly monday, CancellationToken ct)
    {
        var eligible = await GetEligibleMovieIdsAsync(ct);
        if (eligible.Count == 0)
        {
            Log.Warning("[DateNight] No eligible movies — cycle for week of {Monday} not issued", monday);
            return null;
        }

        var draw = eligible.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
        var deadline = ToUtc(monday.AddDays(3), new TimeOnly(23, 59, 59)); // Thursday

        var cycle = new WeeklyCycle(
            monday.ToString("yyyy-MM-dd"), draw, DateTime.UtcNow, deadline, "Active",
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            null, null, null);

        SaveCycle(cycle);
        Log.Information("[DateNight] Cycle issued for week of {Monday} — {Count} movies drawn", monday, draw.Count);
        return cycle;
    }

    /// <summary>Pool movies (from Radarr, via the availability service) eligible for a
    /// fresh draw: obtainable (already on disk, or verified available), not permanently
    /// excluded (watched / never-show), and not currently in a disagreement cooling-off.</summary>
    private async Task<List<int>> GetEligibleMovieIdsAsync(CancellationToken ct)
    {
        var pool = await _availability.GetPoolMoviesAsync(ct);
        var availability = _availability.GetAvailability();
        var lists = GetLists();
        var coolingOffCutoff = DateTime.UtcNow - CoolingOff;

        return pool.OfType<JsonObject>()
            .Where(m => (int?)m["id"] is int && IsEligible(m))
            .Select(m => (int)m["id"]!)
            .ToList();

        bool IsEligible(JsonObject movie)
        {
            var id = (int)movie["id"]!;
            var hasFile = (bool?)movie["hasFile"] ?? false;
            var isAvailable = availability.TryGetValue(id, out var a) && a.IsAvailable;
            if (!hasFile && !isAvailable) return false;

            if (lists.TryGetValue(id, out var entry))
            {
                if (entry.NeverShowAgain) return false;
                if (entry.Watched) return false;
                if (entry.LastDisagreedUtc is DateTime disagreed && disagreed > coolingOffCutoff) return false;
            }

            return true;
        }
    }

    /// <summary>Runs at the deadline: both people must have voted on every drawn movie to
    /// resolve at all; among mutual thumbs-ups, one is picked at random. Kept distinct from
    /// "cancelled" (someone didn't finish voting) because they mean different things to an
    /// admin reading the panel, even though both are "no date night this week" to Mom and Dad.</summary>
    private WeeklyCycle Resolve(WeeklyCycle cycle)
    {
        var everyoneVoted = HouseholdOwners.Names
            .Where(IsAudience)
            .All(p => cycle.Votes.TryGetValue(p, out var votes)
                      && cycle.MovieIds.All(votes.ContainsKey));

        if (!everyoneVoted)
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

        var lists = GetLists();
        foreach (var id in mutual.Where(id => id != picked))
        {
            var entry = lists.TryGetValue(id, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
            lists[id] = entry with { LastApprovedUtc = DateTime.UtcNow };
        }
        SaveLists(lists);

        Log.Information("[DateNight] Cycle {CycleId} resolved — movie {Picked} picked from {Count} mutual approvals",
            cycle.CycleId, picked, mutual.Count);
        return cycle with
        {
            Status = "Resolved",
            ResolvedMovieId = picked,
            ResolvedUtc = DateTime.UtcNow,
            Schedule = new ScheduleState("AwaitingProposal", null, [], null, null)
        };
    }

    private static bool IsAudience(string person) => !string.Equals(person, "Paul", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records one person's vote on one of this week's drawn movies. Side effects
    /// on the permanent lists fire immediately — not deferred to resolution — since a
    /// never-show or disagreement is true regardless of how the rest of the week plays out.</summary>
    public async Task<WeeklyCycle> CastVoteAsync(string person, int movieId, string vote, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad vote.");
        if (!ValidVotes.Contains(vote))
            throw new ArgumentException($"Unrecognized vote '{vote}'.", nameof(vote));

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await AdvanceAsync(ct)
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
            SaveCycle(cycle);

            if (vote.Equals("Never", StringComparison.OrdinalIgnoreCase) || vote.Equals("Down", StringComparison.OrdinalIgnoreCase))
            {
                var lists = GetLists();
                var entry = lists.TryGetValue(movieId, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
                entry = vote.Equals("Never", StringComparison.OrdinalIgnoreCase)
                    ? entry with { NeverShowAgain = true, NeverShowAgainUtc = DateTime.UtcNow }
                    : entry with { LastDisagreedUtc = DateTime.UtcNow };
                lists[movieId] = entry;
                SaveLists(lists);
            }

            Log.Information("[DateNight] {Person} voted {Vote} on movie {MovieId}", person, vote, movieId);
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether the flyer is owed to this person today — active week, they
    /// haven't voted on all three yet, and it hasn't already been shown today
    /// (Hawaii date). Pure/sync: takes the already-advanced cycle rather than
    /// re-fetching, since callers (the cycle read endpoint) already have one.</summary>
    public bool IsFlyerOwedToday(string person, WeeklyCycle cycle)
    {
        if (cycle.Status != "Active") return false;

        var votes = cycle.Votes.TryGetValue(person, out var v) ? v : null;
        if (votes is not null && cycle.MovieIds.All(votes.ContainsKey)) return false;

        if (!cycle.LastFlyerShownUtc.TryGetValue(person, out var last)) return true;
        return HawaiiDate(last) != HawaiiDate(DateTime.UtcNow);
    }

    private static DateOnly HawaiiDate(DateTime utc) =>
        DateOnly.FromDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(HawaiiOffset).Date);

    public async Task RecordFlyerShownAsync(string person, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await AdvanceAsync(ct);
            if (cycle is null) return;

            var shown = new Dictionary<string, DateTime>(cycle.LastFlyerShownUtc, StringComparer.OrdinalIgnoreCase)
            {
                [person] = DateTime.UtcNow
            };
            SaveCycle(cycle with { LastFlyerShownUtc = shown });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>First step of the schedule handshake — either person can be the one
    /// to propose, whoever gets there first. Requires a picked movie and no proposal
    /// already outstanding.</summary>
    public async Task<WeeklyCycle> ProposeScheduleAsync(string person, List<ProposedSlot> slots, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");
        if (slots is not { Count: > 0 })
            throw new ArgumentException("At least one slot must be proposed.", nameof(slots));

        await _lock.WaitAsync(ct);
        try
        {
            var cycle = await AdvanceAsync(ct)
                ?? throw new InvalidOperationException("No active week to schedule.");
            if (cycle.Status != "Resolved" || cycle.Schedule is null)
                throw new InvalidOperationException("No movie has been picked yet this week.");
            if (cycle.Schedule.Status != "AwaitingProposal")
                throw new InvalidOperationException("A schedule has already been proposed this week.");

            cycle = cycle with
            {
                Schedule = cycle.Schedule with { Status = "AwaitingApproval", ProposedBy = person, ProposedSlots = slots }
            };
            SaveCycle(cycle);
            Log.Information("[DateNight] {Person} proposed {Count} slot(s) for this week's date night", person, slots.Count);
            return cycle;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Second step — the *other* person (not the proposer) picks exactly one
    /// of the proposed slots. Locking in is what triggers the actual download; nothing
    /// downloads before this point, per spec.</summary>
    public async Task<WeeklyCycle> ApproveScheduleAsync(string person, ProposedSlot slot, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");

        WeeklyCycle cycle;
        await _lock.WaitAsync(ct);
        try
        {
            cycle = await AdvanceAsync(ct)
                ?? throw new InvalidOperationException("No active week to schedule.");
            if (cycle.Schedule?.Status != "AwaitingApproval")
                throw new InvalidOperationException("There's no proposal waiting on approval.");
            if (string.Equals(cycle.Schedule.ProposedBy, person, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The other person needs to approve — you proposed this one.");
            if (!cycle.Schedule.ProposedSlots.Any(s => s.Date == slot.Date && s.Time == slot.Time))
                throw new InvalidOperationException("That slot wasn't one of the proposed options.");

            cycle = cycle with
            {
                Schedule = cycle.Schedule with { Status = "Locked", LockedSlot = slot, LockedUtc = DateTime.UtcNow }
            };
            SaveCycle(cycle);
            Log.Information("[DateNight] {Person} approved {Date} {Time} — locking in movie {MovieId}",
                person, slot.Date, slot.Time, cycle.ResolvedMovieId);
        }
        finally
        {
            _lock.Release();
        }

        // Outside the lock — this is a slow network round-trip to Radarr, and a
        // hiccup here shouldn't undo the lock-in Mom and Dad just agreed to.
        if (cycle.ResolvedMovieId is int movieId)
            await TriggerDownloadAsync(movieId, ct);

        return cycle;
    }

    /// <summary>Grabs the best obtainable release for a just-locked movie, reusing the
    /// exact same "would we actually accept this" judgment the availability scan uses
    /// (<see cref="DateNightAvailabilityService.IsObtainable"/>) rather than re-deriving
    /// it. Best-effort: setting the movie monitored means Radarr will keep trying on its
    /// own schedule even if this immediate grab attempt finds nothing or fails outright.</summary>
    private async Task TriggerDownloadAsync(int movieId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
            await radarr.EditMoviesAsync([movieId], monitored: true, ct: ct);

            var releases = await radarr.SearchReleasesAsync(movieId, ct);
            var best = releases.OfType<JsonObject>().FirstOrDefault(DateNightAvailabilityService.IsObtainable);
            if (best is not null)
                await radarr.GrabReleaseAsync(best, ct);
            else
                Log.Warning("[DateNight] Lock-in for movie {MovieId} found no obtainable release right now — " +
                            "it's monitored, so Radarr will keep trying", movieId);
        }
        catch (Exception ex)
        {
            Log.Warning("[DateNight] Triggering download for movie {MovieId} failed: {Message}", movieId, ex.Message);
        }
    }

    /// <summary>Either person can cancel — before lock-in (undoes a pending proposal)
    /// or after (also unmonitors the movie again, since a cancelled date night
    /// shouldn't leave a rogue download running against the pool's late-download design).</summary>
    public async Task<WeeklyCycle> CancelScheduleAsync(string person, CancellationToken ct = default)
    {
        if (!IsAudience(person))
            throw new InvalidOperationException("Only Mom and Dad schedule.");

        WeeklyCycle cycle;
        bool wasLocked;
        await _lock.WaitAsync(ct);
        try
        {
            cycle = await AdvanceAsync(ct)
                ?? throw new InvalidOperationException("No active week to cancel.");
            if (cycle.Schedule is null || cycle.Schedule.Status is "Cancelled" or "AwaitingProposal")
                throw new InvalidOperationException("There's nothing scheduled to cancel.");

            wasLocked = cycle.Schedule.Status == "Locked";
            cycle = cycle with { Schedule = cycle.Schedule with { Status = "Cancelled" } };
            SaveCycle(cycle);
            Log.Information("[DateNight] {Person} cancelled this week's date night", person);
        }
        finally
        {
            _lock.Release();
        }

        if (wasLocked && cycle.ResolvedMovieId is int movieId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();
                await radarr.EditMoviesAsync([movieId], monitored: false, ct: ct);
            }
            catch (Exception ex)
            {
                Log.Warning("[DateNight] Could not unmonitor movie {MovieId} after cancelling: {Message}", movieId, ex.Message);
            }
        }

        return cycle;
    }

    private static DateTime ParseSlotToUtc(ProposedSlot slot) =>
        ToUtc(DateOnly.Parse(slot.Date), TimeOnly.Parse(slot.Time));

    /// <summary>Cheap, synchronous, no lock — meant to be polled frequently (every
    /// 30-60s, app-wide) to drive the countdown popup, so it reads the persisted
    /// cycle directly rather than going through the lazy-advance machinery.</summary>
    public ShowtimeStatus GetShowtimeStatus()
    {
        var cycle = LoadCycle();
        if (cycle?.Schedule?.Status != "Locked" || cycle.Schedule.LockedSlot is null || cycle.ResolvedMovieId is null)
            return new ShowtimeStatus(false, null, null);

        var showtimeUtc = ParseSlotToUtc(cycle.Schedule.LockedSlot);
        var now = DateTime.UtcNow;
        // 10 minutes before through 2 hours after — the tail is a safety net so the
        // countdown doesn't hang open forever if left on screen; not called for by the
        // spec, just a practical bound of my own.
        var imminent = now >= showtimeUtc.AddMinutes(-10) && now <= showtimeUtc.AddHours(2);
        return new ShowtimeStatus(imminent, cycle.ResolvedMovieId, showtimeUtc);
    }

    /// <summary>The manual "done watching" confirmation: deletes the file, returns the
    /// movie to unmonitored, drops the pool tag, and marks it permanently watched. The
    /// movie record itself survives — it now shows in the regular library as a
    /// not-ready tile with a manual get button, per the spec's graduation stage.</summary>
    public async Task MarkWatchedAsync(int movieId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var radarr = scope.ServiceProvider.GetRequiredService<IRadarrService>();

        var movie = await radarr.GetMovieAsync(movieId, ct);
        if ((int?)movie?["movieFile"]?["id"] is int movieFileId)
            await radarr.DeleteMovieFileAsync(movieFileId, ct);

        var poolTagId = await radarr.EnsureTagAsync(DateNight.PoolTag, ct);
        await radarr.EditMoviesAsync([movieId], monitored: false, removeTagIds: [poolTagId], ct: ct);

        var lists = GetLists();
        var entry = lists.TryGetValue(movieId, out var e) ? e : new MovieListEntry(false, null, false, null, null, null);
        lists[movieId] = entry with { Watched = true, WatchedUtc = DateTime.UtcNow };
        SaveLists(lists);

        Log.Information("[DateNight] Movie {MovieId} marked watched — file removed, unmonitored, pool tag removed", movieId);
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

    public Dictionary<int, MovieListEntry> GetLists()
    {
        var json = _db.GetState(ListsStateKey);
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

    /// <summary>Clears a movie's never-show and cooling-off flags — the recovery path for
    /// a mis-tap. Watched and approved history are left alone; those aren't mistakes to undo.</summary>
    public void RestoreMovie(int movieId)
    {
        var lists = GetLists();
        if (!lists.TryGetValue(movieId, out var entry)) return;
        lists[movieId] = entry with { NeverShowAgain = false, NeverShowAgainUtc = null, LastDisagreedUtc = null };
        SaveLists(lists);
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
    /// without waiting for Thursday.</summary>
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

    private WeeklyCycle? LoadCycle()
    {
        var json = _db.GetState(CycleStateKey);
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

    private void SaveCycle(WeeklyCycle cycle) =>
        _db.SetState(CycleStateKey, JsonSerializer.Serialize(cycle, JsonOptions));

    private void SaveLists(Dictionary<int, MovieListEntry> lists) =>
        _db.SetState(ListsStateKey, JsonSerializer.Serialize(lists, JsonOptions));
}
