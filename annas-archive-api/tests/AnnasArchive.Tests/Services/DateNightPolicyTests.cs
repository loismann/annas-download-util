using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// These rules used to live inside a 1522-line service that owns a database, a scope
/// factory, two collaborating services and a semaphore, and they read
/// <see cref="DateTime.UtcNow"/> directly. Asking "is the flyer owed on the third day?"
/// meant standing up the whole service, and asking it *at a specific instant* was not
/// possible at all.
///
/// Every rule here turns on a Hawaii <em>date</em> boundary, ten hours off UTC. The
/// tests that matter most are the ones that put two instants on either side of it —
/// exactly the case the old shape could not express.
/// </summary>
public class DateNightPolicyTests
{
    /// <summary>Monday. The whole calendar week hangs off this date.</summary>
    private static readonly DateOnly Monday = new(2026, 7, 27);

    // ─── Hawaii calendar ──────────────────────────────────────────────────

    [Fact]
    public void WeeklyDeadlineUtc_KeepsTheWholeHawaiiWeekOpen()
    {
        var deadline = DateNightPolicy.WeeklyDeadlineUtc(Monday);

        // Sunday August 2 at 11:59:59 PM HST is Monday August 3 at 09:59:59 UTC.
        deadline.Should().Be(new DateTime(2026, 8, 3, 9, 59, 59, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(2026, 7, 27)] // Monday itself
    [InlineData(2026, 7, 28)] // Tuesday
    [InlineData(2026, 8, 2)]  // Sunday, the far end of the week
    public void MondayOf_AnchorsEveryDayOfTheWeekToTheSameMonday(int year, int month, int day)
    {
        var hawaiiNoon = new DateTimeOffset(
            new DateTime(year, month, day, 12, 0, 0), DateNightPolicy.HawaiiOffset);

        DateNightPolicy.MondayOf(hawaiiNoon).Should().Be(Monday);
    }

    /// <summary>
    /// The pair that justifies the whole "pass the clock in" change. Both instants are
    /// August 3rd in UTC; only one of them is August 3rd in Hawaii. A once-per-day rule
    /// that used UTC dates would fire twice in one Hawaii evening.
    /// </summary>
    [Fact]
    public void HawaiiDate_SplitsOneUtcDayAcrossTwoHawaiiDays()
    {
        DateNightPolicy.HawaiiDate(new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 2));

        DateNightPolicy.HawaiiDate(new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 8, 3));
    }

    /// <summary>And the mirror image: two different UTC days that are one Hawaii day.</summary>
    [Fact]
    public void HawaiiDate_JoinsTwoUtcDaysIntoOneHawaiiDay()
    {
        var evening = DateNightPolicy.HawaiiDate(new DateTime(2026, 8, 2, 23, 0, 0, DateTimeKind.Utc));
        var lateNight = DateNightPolicy.HawaiiDate(new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc));

        evening.Should().Be(lateNight).And.Be(new DateOnly(2026, 8, 2));
    }

    [Fact]
    public void ToUtc_ShiftsAHawaiiWallClockTimeForwardByTenHours()
    {
        DateNightPolicy.ToUtc(new DateOnly(2026, 7, 31), new TimeOnly(19, 0))
            .Should().Be(new DateTime(2026, 8, 1, 5, 0, 0, DateTimeKind.Utc));
    }

    // ─── Who votes ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Mom", true)]
    [InlineData("Dad", true)]
    [InlineData("mom", true)]   // the app passes whatever casing the caller used
    [InlineData("Paul", false)] // Paul runs it, he does not vote
    [InlineData("Nobody", false)]
    [InlineData("", false)]
    public void IsAudience_IsEveryHouseholdMemberExceptPaul(string person, bool expected) =>
        DateNightPolicy.IsAudience(person).Should().Be(expected);

    [Fact]
    public void EveryoneVoted_IsFalseUntilBothPeopleHaveRatedEveryDrawnMovie()
    {
        var cycle = ActiveCycle(votes: new()
        {
            ["Mom"] = new() { [1] = "Up", [2] = "Down" },
            ["Dad"] = new() { [1] = "Up" }
        });

        DateNightPolicy.EveryoneVoted(cycle).Should().BeFalse();
    }

    [Fact]
    public void EveryoneVoted_IsTrueOnlyWhenTheLastBallotIsComplete()
    {
        var cycle = ActiveCycle(votes: new()
        {
            ["Mom"] = new() { [1] = "Up", [2] = "Down" },
            ["Dad"] = new() { [1] = "Up", [2] = "Never" }
        });

        DateNightPolicy.EveryoneVoted(cycle).Should().BeTrue();
    }

    /// <summary>Paul is not counted. If he were, the week could never resolve.</summary>
    [Fact]
    public void EveryoneVoted_DoesNotWaitForPaul()
    {
        var cycle = ActiveCycle(votes: new()
        {
            ["Mom"] = new() { [1] = "Up", [2] = "Up" },
            ["Dad"] = new() { [1] = "Up", [2] = "Up" }
        });

        DateNightPolicy.EveryoneVoted(cycle).Should().BeTrue();
    }

    [Theory]
    [InlineData("Up", true)]
    [InlineData("Down", true)]
    [InlineData("Never", true)]
    [InlineData("up", true)]
    [InlineData("Maybe", false)]
    [InlineData("", false)]
    public void IsValidVote_AcceptsOnlyTheThreeBallotOptions(string vote, bool expected) =>
        DateNightPolicy.IsValidVote(vote).Should().Be(expected);

    // ─── Flyer reminders ──────────────────────────────────────────────────

    [Fact]
    public void FlyerReminder_IsOwedWhilePersonHasFewerThanThreePrompts()
    {
        var cycle = ActiveCycle(reminderCounts: new() { ["Mom"] = 2 });

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeTrue();
    }

    [Fact]
    public void FlyerReminder_StopsAfterThreePromptsButCycleRemainsActive()
    {
        var cycle = ActiveCycle(
            reminderCounts: new() { ["Mom"] = DateNightPolicy.MaxFlyerReminderCount });

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeFalse();
        cycle.Status.Should().Be("Active");
    }

    [Fact]
    public void FlyerReminder_CountsAreIndependentForMomAndDad()
    {
        var cycle = ActiveCycle(reminderCounts: new() { ["Mom"] = 3, ["Dad"] = 1 });

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeFalse();
        DateNightPolicy.IsFlyerOwedToday("Dad", cycle, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData("Resolved")]
    [InlineData("Cancelled")]
    [InlineData("NoMatch")]
    public void FlyerReminder_IsOnlyOwedDuringAnActiveWeek(string status)
    {
        var cycle = ActiveCycle() with { Status = status };

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeFalse();
    }

    [Fact]
    public void FlyerReminder_StopsOnceThePersonHasVotedOnEverything()
    {
        var cycle = ActiveCycle(
            votes: new() { ["Mom"] = new() { [1] = "Up", [2] = "Down" } },
            schedule: new ScheduleState("AwaitingApproval", "Dad", [], null, null, [], null));

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeFalse();
    }

    /// <summary>
    /// Voting is only half of what the flyer chases. Someone who has finished their
    /// ballot but not yet offered any times is still holding the week up.
    /// </summary>
    [Fact]
    public void FlyerReminder_KeepsGoingWhenTheBallotIsDoneButNoTimesAreProposed()
    {
        var cycle = ActiveCycle(
            votes: new() { ["Mom"] = new() { [1] = "Up", [2] = "Down" } },
            schedule: DateNightPolicy.NewSchedule());

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, Now).Should().BeTrue();
    }

    /// <summary>
    /// The once-a-day rule, on the boundary that actually matters. Shown at 9am UTC
    /// (Hawaii evening of the 2nd), asked again at 9:30am UTC — still the same Hawaii
    /// day, so no second prompt, even though several hours of UTC have passed.
    /// </summary>
    [Fact]
    public void FlyerReminder_IsNotOwedTwiceInOneHawaiiDay()
    {
        var shown = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
        var cycle = ActiveCycle(lastShown: new() { ["Mom"] = shown });

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, shown.AddMinutes(30)).Should().BeFalse();
    }

    /// <summary>One hour later than the case above, and it is a new Hawaii day.</summary>
    [Fact]
    public void FlyerReminder_ComesBackOnceTheHawaiiDateRollsOver()
    {
        var shown = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
        var cycle = ActiveCycle(lastShown: new() { ["Mom"] = shown });

        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, shown.AddHours(1)).Should().BeTrue();
    }

    /// <summary>
    /// The inverse trap: a new UTC day that is still the same Hawaii day. A rule written
    /// against UTC dates would wrongly prompt a second time here.
    /// </summary>
    [Fact]
    public void FlyerReminder_IgnoresAUtcMidnightThatIsNotAHawaiiMidnight()
    {
        var shown = new DateTime(2026, 8, 2, 23, 0, 0, DateTimeKind.Utc);
        var cycle = ActiveCycle(lastShown: new() { ["Mom"] = shown });

        // 09:00 UTC on the 3rd is still the evening of the 2nd in Hawaii.
        DateNightPolicy.IsFlyerOwedToday("Mom", cycle, new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc))
            .Should().BeFalse();
    }

    [Fact]
    public void FlyerReminder_IsOwedToSomeoneWhoHasNeverSeenIt()
    {
        DateNightPolicy.IsFlyerOwedToday("Mom", ActiveCycle(), Now).Should().BeTrue();
    }

    // ─── Schedule reminders ───────────────────────────────────────────────

    private static WeeklyCycle AwaitingApproval(
        string proposedBy = "Dad", Dictionary<string, DateTime>? lastReminder = null) =>
        ActiveCycle() with
        {
            Status = "Resolved",
            Schedule = new ScheduleState(
                "AwaitingApproval", proposedBy, [], null, null, [], null,
                LastReminderShownUtc: lastReminder)
        };

    [Fact]
    public void ScheduleReminder_IsOwedToWhoeverStillOwesAnAnswer()
    {
        DateNightPolicy.IsScheduleReminderOwedToday("Mom", AwaitingApproval(), Now)
            .Should().BeTrue();
    }

    /// <summary>Nobody is reminded to answer themselves.</summary>
    [Fact]
    public void ScheduleReminder_IsNeverOwedToTheProposer()
    {
        DateNightPolicy.IsScheduleReminderOwedToday("Dad", AwaitingApproval(proposedBy: "Dad"), Now)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("AwaitingProposal")]
    [InlineData("Locked")]
    [InlineData("Cancelled")]
    [InlineData("Concluded")]
    public void ScheduleReminder_OnlyAppliesWhileAProposalIsOutstanding(string scheduleStatus)
    {
        var cycle = AwaitingApproval();
        cycle = cycle with { Schedule = cycle.Schedule! with { Status = scheduleStatus } };

        DateNightPolicy.IsScheduleReminderOwedToday("Mom", cycle, Now).Should().BeFalse();
    }

    [Fact]
    public void ScheduleReminder_IsAlsoOncePerHawaiiDay()
    {
        var shown = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
        var cycle = AwaitingApproval(lastReminder: new() { ["Mom"] = shown });

        DateNightPolicy.IsScheduleReminderOwedToday("Mom", cycle, shown.AddMinutes(30)).Should().BeFalse();
        DateNightPolicy.IsScheduleReminderOwedToday("Mom", cycle, shown.AddHours(1)).Should().BeTrue();
    }

    // ─── Proposed slots ───────────────────────────────────────────────────

    /// <summary>Monday of the cycle week, 00:00 UTC — comfortably before every slot below.</summary>
    private static readonly DateTime BeforeTheWeek = new(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);

    private static List<ProposedSlot> Slots(params (string Date, string Time)[] slots) =>
        slots.Select(s => new ProposedSlot(s.Date, s.Time)).ToList();

    [Fact]
    public void ValidateSlots_AcceptsAWellFormedProposal()
    {
        var act = () => DateNightPolicy.ValidateSlots(
            Slots(("2026-07-31", "19:00"), ("2026-08-01", "20:30")), BeforeTheWeek);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSlots_RejectsAnEmptyProposal()
    {
        var act = () => DateNightPolicy.ValidateSlots([], BeforeTheWeek);

        act.Should().Throw<ArgumentException>().WithMessage("*At least one slot*");
    }

    [Fact]
    public void ValidateSlots_RejectsMoreThanSevenDistinctDays()
    {
        var eightDays = Enumerable.Range(0, 8)
            .Select(i => (Date: new DateOnly(2026, 7, 27).AddDays(i).ToString("yyyy-MM-dd"), Time: "19:00"))
            .ToArray();

        var act = () => DateNightPolicy.ValidateSlots(Slots(eightDays), BeforeTheWeek);

        act.Should().Throw<ArgumentException>().WithMessage("*At most 7 days*");
    }

    /// <summary>
    /// The grid is half-hourly, so 7:15 is not a time this form can produce. Enforced
    /// server-side too, since the check exists to stop a hand-rolled API call.
    /// </summary>
    [Fact]
    public void ValidateSlots_RejectsATimeOffTheHalfHourGrid()
    {
        var act = () => DateNightPolicy.ValidateSlots(Slots(("2026-07-31", "19:15")), BeforeTheWeek);

        act.Should().Throw<ArgumentException>().WithMessage("*30-minute boundary*");
    }

    [Theory]
    [InlineData("12:00")] // the earliest allowed
    [InlineData("23:30")] // the latest allowed
    public void ValidateSlots_AcceptsBothEndsOfTheAllowedWindow(string time)
    {
        var act = () => DateNightPolicy.ValidateSlots(Slots(("2026-07-31", time)), BeforeTheWeek);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("11:30")] // half an hour too early
    [InlineData("00:00")] // midnight, the far side of the window
    public void ValidateSlots_RejectsTimesOutsideTheNoonToMidnightWindow(string time)
    {
        var act = () => DateNightPolicy.ValidateSlots(Slots(("2026-07-31", time)), BeforeTheWeek);

        act.Should().Throw<ArgumentException>().WithMessage("*noon*");
    }

    [Fact]
    public void ValidateSlots_RejectsASlotThatHasAlreadyPassed()
    {
        // 19:00 Hawaii on the 31st is 05:00 UTC on August 1st; ask an hour after that.
        var act = () => DateNightPolicy.ValidateSlots(
            Slots(("2026-07-31", "19:00")), new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc));

        act.Should().Throw<ArgumentException>().WithMessage("*must be in the future*");
    }

    /// <summary>
    /// Exactly on the instant, which is the one case "in the future" has to rule on and
    /// the one an off-by-one would get wrong. A slot starting right now has not passed,
    /// but it is not in the future either, and proposing it would mean agreeing to a
    /// Date Night that has already begun.
    /// </summary>
    [Fact]
    public void ValidateSlots_RejectsASlotStartingAtThisExactInstant()
    {
        // 19:00 Hawaii on Jul 31 is exactly 05:00 UTC on Aug 1.
        var act = () => DateNightPolicy.ValidateSlots(
            Slots(("2026-07-31", "19:00")), new DateTime(2026, 8, 1, 5, 0, 0, DateTimeKind.Utc));

        act.Should().Throw<ArgumentException>().WithMessage("*must be in the future*");
    }

    /// <summary>
    /// The boundary of "already passed" is the slot's own instant, and that instant is
    /// ten hours off the wall-clock time it displays — the reason this is worth pinning.
    /// </summary>
    [Fact]
    public void ValidateSlots_TreatsASlotStillHoursAwayInHawaiiAsFuture()
    {
        // 04:59 UTC on Aug 1 is 18:59 Hawaii on Jul 31 — one minute before a 19:00 slot.
        var act = () => DateNightPolicy.ValidateSlots(
            Slots(("2026-07-31", "19:00")), new DateTime(2026, 8, 1, 4, 59, 0, DateTimeKind.Utc));

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSlots_RejectsAnUnparseableDate()
    {
        var act = () => DateNightPolicy.ValidateSlots(Slots(("not-a-date", "19:00")), BeforeTheWeek);

        act.Should().Throw<ArgumentException>().WithMessage("*Invalid date*");
    }

    [Fact]
    public void ValidateSlotsForCycle_AcceptsSlotsInsideTheCycleWeek()
    {
        var act = () => DateNightPolicy.ValidateSlotsForCycle(
            ActiveCycle(), Slots(("2026-07-27", "19:00"), ("2026-08-02", "19:00")), isTest: false);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Without this, a proposal made late in one week could schedule Date Night into the
    /// next week, which would then issue its own cycle — two Date Nights, one of them
    /// invisible to the app that created it.
    /// </summary>
    [Fact]
    public void ValidateSlotsForCycle_RejectsASlotInTheFollowingWeek()
    {
        var act = () => DateNightPolicy.ValidateSlotsForCycle(
            ActiveCycle(), Slots(("2026-08-03", "19:00")), isTest: false);

        act.Should().Throw<ArgumentException>().WithMessage("*within this Date Night week*");
    }

    /// <summary>The dry run has no calendar week — it keeps a rolling seven-day window.</summary>
    [Fact]
    public void ValidateSlotsForCycle_LetsTheDryRunProposeAnyDate()
    {
        var act = () => DateNightPolicy.ValidateSlotsForCycle(
            ActiveCycle() with { CycleId = "test" }, Slots(("2027-01-01", "19:00")), isTest: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSlotsForCycle_RefusesACycleWhoseIdIsNotACalendarWeek()
    {
        var act = () => DateNightPolicy.ValidateSlotsForCycle(
            ActiveCycle() with { CycleId = "test" }, Slots(("2026-07-31", "19:00")), isTest: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid calendar week*");
    }

    // ─── Who is acting ────────────────────────────────────────────────────

    /// <summary>
    /// The header is matched case-insensitively, so it must not be handed back as
    /// typed. The returned string becomes the key of this person's ballot, flyer and
    /// reminder maps, and every other lookup uses the canonical "Mom" — so "mom" here
    /// stored a real vote at an address nothing ever reads again.
    /// </summary>
    [Theory]
    [InlineData("mom", "Mom")]
    [InlineData("MOM", "Mom")]
    [InlineData("dAd", "Dad")]
    [InlineData("Mom", "Mom")]
    public void A_view_as_header_is_canonicalized_rather_than_echoed_back(string header, string expected)
    {
        DateNightPolicy.ResolveViewer("Paul", header).Should().Be((expected, true));
    }

    /// <summary>
    /// The whole safety property: only Paul may view as anyone. Mom sending the
    /// header must stay Mom, or either audience member could cast the other's ballot
    /// by adding a header to their own session.
    /// </summary>
    [Theory]
    [InlineData("Mom", "Dad")]
    [InlineData("Dad", "Mom")]
    [InlineData("Stranger", "Mom")]
    public void Only_Paul_may_view_as_someone_else(string real, string header)
    {
        DateNightPolicy.ResolveViewer(real, header).Should().Be((real, false));
    }

    /// <summary>
    /// A header naming nobody impersonable leaves Paul as Paul — and, just as
    /// importantly, leaves IsTest false, so an unrecognised header cannot quietly
    /// route a real action at the dry-run cycle or the reverse.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Paul")]
    [InlineData("Nobody")]
    public void A_header_naming_nobody_impersonable_leaves_Paul_as_himself(string? header)
    {
        DateNightPolicy.ResolveViewer("Paul", header).Should().Be(("Paul", false));
    }

    // ─── Surviving storage ────────────────────────────────────────────────

    /// <summary>
    /// The premise, proven rather than asserted: a cycle written with
    /// case-insensitive maps comes back from JSON with case-<i>sensitive</i> ones.
    /// The raw round-trip cannot find the ballot; the canonicalized one can. If
    /// <c>Deserialize</c> ever starts preserving comparers this test says so by
    /// failing its first half.
    /// </summary>
    [Fact]
    public void A_comparer_does_not_survive_JSON_which_is_why_the_re_key_exists()
    {
        var stored = ActiveCycle(votes: new() { ["mom"] = new() { [1] = "Up", [2] = "Up" } });

        var raw = RoundTrip(stored);
        DateNightPolicy.BallotComplete("Mom", raw).Should().BeFalse(
            "this is the bug — the vote is present and unreachable");

        DateNightPolicy.BallotComplete("Mom", DateNightPolicy.WithCanonicalPeople(raw)!)
            .Should().BeTrue("re-keying restores the comparer the write intended");
    }

    /// <summary>Every person-keyed map on a cycle, not just the ballot — each one is
    /// read with a canonical name somewhere.</summary>
    [Fact]
    public void Every_person_keyed_map_survives_a_round_trip()
    {
        var stored = ActiveCycle(
            votes: new() { ["mom"] = new() { [1] = "Up" } },
            lastShown: new() { ["mom"] = Now },
            reminderCounts: new() { ["mom"] = MaxReminders },
            schedule: DateNightPolicy.NewSchedule() with
            {
                Status = "AwaitingApproval",
                ProposedBy = "Dad",
                LastReminderShownUtc = new() { ["mom"] = Now }
            });

        var cycle = DateNightPolicy.WithCanonicalPeople(RoundTrip(stored))!;

        DateNightPolicy.FlyerReminderCount("Mom", cycle).Should().Be(MaxReminders);
        cycle.LastFlyerShownUtc.Should().ContainKey("Mom");
        cycle.Schedule!.LastReminderShownUtc.Should().ContainKey("Mom");
    }

    /// <summary>Nothing to re-key is not an error — a cycle may have no schedule and
    /// no reminder counts, and loading is the only caller.</summary>
    [Fact]
    public void A_cycle_with_nothing_optional_set_re_keys_without_complaint()
    {
        DateNightPolicy.WithCanonicalPeople(null).Should().BeNull();

        var bare = ActiveCycle() with { Schedule = null, FlyerReminderCounts = null };
        var rekeyed = DateNightPolicy.WithCanonicalPeople(bare)!;

        rekeyed.Schedule.Should().BeNull();
        rekeyed.FlyerReminderCounts.Should().BeNull();
    }

    private const int MaxReminders = DateNightPolicy.MaxFlyerReminderCount;

    /// <summary>Through storage exactly as <c>DateNightCycleService</c> does it.</summary>
    private static WeeklyCycle RoundTrip(WeeklyCycle cycle)
    {
        var options = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web);
        return System.Text.Json.JsonSerializer.Deserialize<WeeklyCycle>(
            System.Text.Json.JsonSerializer.Serialize(cycle, options), options)!;
    }

    // ─── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>Mid-week, so nothing accidentally sits on a boundary unless a test
    /// puts it there deliberately.</summary>
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    private static WeeklyCycle ActiveCycle(
        Dictionary<string, Dictionary<int, string>>? votes = null,
        Dictionary<string, DateTime>? lastShown = null,
        Dictionary<string, int>? reminderCounts = null,
        ScheduleState? schedule = null) =>
        new(
            Monday.ToString("yyyy-MM-dd"),
            [1, 2],
            Now,
            DateNightPolicy.WeeklyDeadlineUtc(Monday),
            "Active",
            new Dictionary<string, Dictionary<int, string>>(votes ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(lastShown ?? [], StringComparer.OrdinalIgnoreCase),
            null,
            null,
            schedule ?? DateNightPolicy.NewSchedule(),
            new Dictionary<string, int>(reminderCounts ?? [], StringComparer.OrdinalIgnoreCase));
}
