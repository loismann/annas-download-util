using System.Diagnostics.CodeAnalysis;
using AnnasArchive.API.Constants;

namespace AnnasArchive.API.Services;

/// <summary>
/// Every Date Night rule that is a pure function of its arguments: the Hawaii
/// calendar, who counts as audience, when a ballot is complete, when a reminder is
/// owed, and what makes a proposed slot legal.
///
/// These lived inside <see cref="DateNightCycleService"/>, a 1522-line class
/// that also owns a database, a scope factory, two collaborating services and a
/// semaphore. The rules themselves need none of that, but sitting there meant the only
/// way to ask "is the flyer owed on the third day?" was to construct the whole service.
///
/// The three time-dependent rules take <c>utcNow</c> as a parameter rather than reading
/// <see cref="DateTime.UtcNow"/> themselves. That is the substantive change: every one
/// of them turns on a Hawaii *date* boundary, and a rule you cannot place on either
/// side of its own boundary is a rule you cannot actually test.
/// </summary>
public static class DateNightPolicy
{
    /// <summary>
    /// Hawaii as a fixed offset rather than a named zone: it never observes DST, so
    /// the offset is exactly as correct as a lookup and does not depend on the
    /// container image shipping IANA tzdata.
    /// </summary>
    public static readonly TimeSpan HawaiiOffset = TimeSpan.FromHours(-10);

    /// <summary>Each person gets at most this many once-daily flyer prompts. The flyer
    /// stays manually reachable afterwards — this only stops the nagging.</summary>
    public const int MaxFlyerReminderCount = 3;

    /// <summary>How long a disagreed-on movie stays out of the draw. Public so the
    /// admin endpoint reports "still cooling off" the same way issuance decides it,
    /// rather than re-hardcoding four weeks.</summary>
    public static readonly TimeSpan CoolingOff = TimeSpan.FromDays(28);

    /// <summary>Earliest allowed slot time — "after 12pm" per spec.</summary>
    public static readonly TimeOnly EarliestSlotTime = new(12, 0);

    /// <summary>Latest allowed slot time — "before 12am" per spec, on the same
    /// 30-minute grid as everything else.</summary>
    public static readonly TimeOnly LatestSlotTime = new(23, 30);

    private static readonly HashSet<string> ValidVotes =
        new(StringComparer.OrdinalIgnoreCase) { "Up", "Down", "Never" };

    public static bool IsValidVote(string vote) => ValidVotes.Contains(vote);

    // ─── Hawaii calendar ──────────────────────────────────────────────────

    public static DateTimeOffset NowHawaii(DateTime utcNow) =>
        new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToOffset(HawaiiOffset);

    /// <summary>The Monday that starts the Hawaii calendar week containing this instant.</summary>
    public static DateOnly MondayOf(DateTimeOffset hawaiiNow)
    {
        var date = DateOnly.FromDateTime(hawaiiNow.Date);
        var back = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-back);
    }

    /// <summary>The Hawaii calendar date a UTC instant falls on. Reminders are
    /// "once per day" in Hawaii terms, not in UTC terms — the two disagree for the
    /// ten hours either side of Hawaii midnight.</summary>
    public static DateOnly HawaiiDate(DateTime utc) =>
        DateOnly.FromDateTime(NowHawaii(utc).Date);

    public static DateTime ToUtc(DateOnly hawaiiDate, TimeOnly hawaiiTime) =>
        new DateTimeOffset(hawaiiDate.ToDateTime(hawaiiTime), HawaiiOffset).UtcDateTime;

    /// <summary>The full Monday-Sunday calendar week remains available for voting
    /// and scheduling. Sunday at 11:59:59 PM Hawaii time is the final fallback for
    /// an unfinished ballot.</summary>
    public static DateTime WeeklyDeadlineUtc(DateOnly monday) =>
        ToUtc(monday.AddDays(6), new TimeOnly(23, 59, 59));

    // ─── Who votes ────────────────────────────────────────────────────────

    /// <summary>Mom and Dad watch; Paul runs the thing. Written as "a household
    /// member who is not Paul" rather than a literal pair so adding a fourth person
    /// does not silently exclude them.</summary>
    /// <remarks>Accepts null because callers hand over an unresolved identity
    /// directly; nobody is not audience, which is the same answer as a stranger.</remarks>
    public static bool IsAudience(string? person) =>
        person is not null && HouseholdOwners.Names.Any(n =>
            !string.Equals(n, "Paul", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(n, person, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Who is acting, and whether this is a dry run — from the JWT-verified identity
    /// and the raw <c>X-Date-Night-As</c> header.
    ///
    /// <para>Admin-only "view as", so Paul can click through the real Mom/Dad UI from
    /// his own session instead of the admin bypass panel. It only applies when the
    /// <b>real</b> identity is Paul, so Mom and Dad cannot spoof each other by sending
    /// the header themselves.</para>
    ///
    /// <para>The header is <b>canonicalized, never echoed back raw</b>. It is matched
    /// case-insensitively, so a header of "mom" used to return "mom" — and that string
    /// became the key of this person's ballot, flyer and reminder maps, which every
    /// other lookup reads as "Mom". The vote was stored and then invisible.</para>
    ///
    /// <para>Impersonating doubles as "this is a dry run": <c>IsTest</c> is true exactly
    /// when the override applied, which callers use to route every action at the
    /// separate test cycle and lists instead of real household state. A real Mom or Dad
    /// session can never produce IsTest=true.</para>
    /// </summary>
    public static (string? Person, bool IsTest) ResolveViewer(string? real, string? impersonation)
    {
        if (!string.Equals(real, "Paul", StringComparison.OrdinalIgnoreCase))
            return (real, false);

        // IsAudience rather than a literal Mom/Dad pair, so a fourth household member
        // is viewable the day they are added rather than silently unimpersonatable.
        return HouseholdOwners.ResolveName(impersonation) is { } viewAs && IsAudience(viewAs)
            ? (viewAs, true)
            : (real, false);
    }

    /// <summary>Whether every audience member has voted on every one of this week's
    /// drawn movies — decides Resolved vs. Cancelled at the deadline, and whether a
    /// just-cast vote was the last one needed to resolve immediately.</summary>
    public static bool EveryoneVoted(WeeklyCycle cycle) =>
        HouseholdOwners.Names
            .Where(IsAudience)
            .All(p => cycle.Votes.TryGetValue(p, out var votes)
                      && cycle.MovieIds.All(votes.ContainsKey));

    /// <summary>Whether this person has voted on every movie in the draw.</summary>
    public static bool BallotComplete(string person, WeeklyCycle cycle) =>
        cycle.Votes.TryGetValue(person, out var votes) && cycle.MovieIds.All(votes.ContainsKey);

    // ─── The storage boundary ─────────────────────────────────────────────

    /// <summary>
    /// Re-keys a cycle's four person-keyed dictionaries case-insensitively, on the
    /// way back from storage.
    ///
    /// <para><b>A comparer does not survive JSON.</b> Every one of these maps is
    /// written through <c>StringComparer.OrdinalIgnoreCase</c>, but
    /// <c>Deserialize</c> hands back plain, case-sensitive dictionaries — so the
    /// comparer only ever protected the write, never a later read. Any key stored
    /// under a casing other than the canonical one was then unreachable: the data
    /// was there and the cycle behaved as though it were not.</para>
    ///
    /// <para>Applied at the single point a cycle is loaded, so no caller has to
    /// remember to. Canonicalizing identities at the edges (see
    /// <see cref="ResolveViewer"/>) stops odd casings arriving; this stops the ones
    /// already stored, and both are needed.</para>
    /// </summary>
    public static WeeklyCycle? WithCanonicalPeople(WeeklyCycle? cycle) =>
        cycle is null ? null : cycle with
        {
            Votes = ByPerson(cycle.Votes),
            LastFlyerShownUtc = ByPerson(cycle.LastFlyerShownUtc),
            FlyerReminderCounts = ByPerson(cycle.FlyerReminderCounts),
            Schedule = cycle.Schedule is null ? null : cycle.Schedule with
            {
                LastReminderShownUtc = ByPerson(cycle.Schedule.LastReminderShownUtc)
            }
        };

    /// <summary>Null in, null out — the optional maps stay optional.</summary>
    [return: NotNullIfNotNull(nameof(map))]
    private static Dictionary<string, T>? ByPerson<T>(Dictionary<string, T>? map) =>
        map is null ? null : new Dictionary<string, T>(map, StringComparer.OrdinalIgnoreCase);

    // ─── Reminders ────────────────────────────────────────────────────────

    /// <summary>Whether the flyer is owed to this person today — active week, they
    /// still owe either movie votes or their initial time proposal, it has not already
    /// been shown today (Hawaii date), and they have received fewer than three daily
    /// prompts.</summary>
    public static bool IsFlyerOwedToday(string person, WeeklyCycle cycle, DateTime utcNow)
    {
        if (cycle.Status != "Active") return false;

        var ballotComplete = BallotComplete(person, cycle);
        var stillNeedsInitialTimes = ballotComplete && cycle.Schedule?.Status == "AwaitingProposal";
        if (ballotComplete && !stillNeedsInitialTimes) return false;

        if (FlyerReminderCount(person, cycle) >= MaxFlyerReminderCount) return false;

        if (!cycle.LastFlyerShownUtc.TryGetValue(person, out var last)) return true;
        return HawaiiDate(last) != HawaiiDate(utcNow);
    }

    public static int FlyerReminderCount(string person, WeeklyCycle cycle) =>
        cycle.FlyerReminderCounts is not null &&
        cycle.FlyerReminderCounts.TryGetValue(person, out var count)
            ? count
            : 0;

    /// <summary>The responder to a schedule proposal gets one gentle prompt per
    /// Hawaii day until they approve, counter, or cancel. Proposal changes replace
    /// the reminder map, so the new state can surface immediately.</summary>
    public static bool IsScheduleReminderOwedToday(string person, WeeklyCycle cycle, DateTime utcNow)
    {
        var schedule = cycle.Schedule;
        if (cycle.Status != "Resolved" || schedule?.Status != "AwaitingApproval") return false;
        if (string.Equals(schedule.ProposedBy, person, StringComparison.OrdinalIgnoreCase)) return false;

        if (schedule.LastReminderShownUtc is null ||
            !schedule.LastReminderShownUtc.TryGetValue(person, out var last))
            return true;
        return HawaiiDate(last) != HawaiiDate(utcNow);
    }

    // ─── Proposed slots ───────────────────────────────────────────────────

    public static ScheduleState NewSchedule() =>
        new("AwaitingProposal", null, [], null, null, [], null);

    /// <summary>Validates a proposed slot list against the shape the scheduling form
    /// is supposed to produce — enforced here too, not just in the UI, so a stray API
    /// call can't exceed it. Every time is on a 30-minute boundary within
    /// [noon, 11:30pm] and every resulting slot must still be in the future.</summary>
    public static void ValidateSlots(List<ProposedSlot> slots, DateTime utcNow)
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
            if (ToUtc(date, time) <= utcNow)
                throw new ArgumentException($"Slot '{slot.Date} {slot.Time}' must be in the future.", nameof(slots));
        }
    }

    /// <summary>A real cycle owns exactly one Hawaii calendar week, Monday through
    /// Sunday. Keeping every proposal inside that range prevents an old week's
    /// negotiation from creating a second Date Night in the following week. The
    /// isolated dry run has no calendar week and intentionally keeps its rolling
    /// seven-day test window.</summary>
    public static void ValidateSlotsForCycle(WeeklyCycle cycle, List<ProposedSlot> slots, bool isTest)
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
}
