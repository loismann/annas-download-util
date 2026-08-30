using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The Date Night state machine: who may vote, what a vote does to the permanent
/// lists, when a week resolves, and the propose/approve handshake that ends in a
/// download.
///
/// <para>1,396 lines and the largest file in the repo, with no test naming it.
/// <c>DateNightPolicy</c> already covers the pure calendar and ballot rules; what
/// had no coverage at all is everything that <i>writes</i> — and those are the
/// paths where a wrong answer is not a visible error but a movie quietly retired
/// forever, a week that resolves to the wrong film, or a showtime Mom and Dad
/// agreed on that silently un-agrees itself.</para>
///
/// <para><b>How these run.</b> Against a real SQLite database in a temp directory,
/// with the cycle seeded directly into state. Seeding is what avoids the draw
/// path — the only part of this service that needs Radarr and the summary
/// generator — so the availability and summary services are never touched and are
/// passed as null deliberately. The scope factory throws, which exercises the
/// "Radarr is unreachable" branch rather than avoiding it.</para>
/// </summary>
public sealed class DateNightCycleServiceTests : IDisposable
{
    private const string Mom = "Mom";
    private const string Dad = "Dad";
    private const string Paul = "Paul";
    private const string TestCycleKey = "date-night:test-cycle";
    private const string TestListsKey = "date-night:test-lists";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "datenight-tests", Guid.NewGuid().ToString("N"));

    private readonly AppDatabase _db;
    private readonly DateNightCycleService _svc;

    public DateNightCycleServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "app.db")
            })
            .Build();

        _db = new AppDatabase(config);

        // Availability and summaries are only reached from the draw path, which
        // seeding avoids entirely. Null rather than a stub so that if a future
        // change makes one of these paths draw, it fails loudly here instead of
        // passing against a fake that answers something plausible.
        _svc = new DateNightCycleService(null!, null!, _db, new UnreachableRadarr());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>
    /// Stands in for a Radarr that cannot be reached. <c>TriggerDownloadAsync</c>
    /// catches everything and records "Failed", so this drives the real failure
    /// branch rather than dodging it.
    /// </summary>
    private sealed class UnreachableRadarr : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Radarr is unreachable");
    }

    // ------------------------------------------------------------- seeding

    private void Seed(WeeklyCycle cycle) =>
        _db.SetState(TestCycleKey, JsonSerializer.Serialize(cycle, Json));

    private WeeklyCycle Stored() =>
        JsonSerializer.Deserialize<WeeklyCycle>(_db.GetState(TestCycleKey)!, Json)!;

    private Dictionary<int, MovieListEntry> StoredLists() =>
        JsonSerializer.Deserialize<Dictionary<int, MovieListEntry>>(
            _db.GetState(TestListsKey) ?? "{}", Json)!;

    private static Dictionary<string, Dictionary<int, string>> Ballots(
        params (string Person, (int Movie, string Vote)[] Votes)[] entries)
    {
        var all = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (person, votes) in entries)
            all[person] = votes.ToDictionary(v => v.Movie, v => v.Vote);
        return all;
    }

    private static WeeklyCycle Cycle(
        List<int>? movies = null,
        string status = "Active",
        Dictionary<string, Dictionary<int, string>>? votes = null,
        int? resolvedMovieId = null,
        ScheduleState? schedule = null) =>
        new("test",
            movies ?? [1, 2],
            DateTime.UtcNow,
            DateTime.UtcNow.AddYears(10),
            status,
            votes ?? new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            resolvedMovieId,
            resolvedMovieId is null ? null : DateTime.UtcNow,
            schedule ?? DateNightPolicy.NewSchedule());

    /// <summary>A slot comfortably in the future, inside the noon–11:30pm Hawaii window.</summary>
    private static ProposedSlot Slot(int daysAhead = 3, string time = "19:00") =>
        new(DateNightPolicy.HawaiiDate(DateTime.UtcNow).AddDays(daysAhead).ToString("yyyy-MM-dd"), time);

    // ------------------------------------------------------- who may vote

    /// <summary>
    /// Paul runs Date Night; Mom and Dad watch it. A vote from the admin would
    /// count toward "everyone voted" and change which film gets picked.
    /// </summary>
    [Fact]
    public async Task TheAdminIsNotAnAudienceMemberAndCannotVote()
    {
        Seed(Cycle());

        var act = async () => await _svc.CastVoteAsync(Paul, 1, "Up", isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Mom and Dad*");
    }

    /// <summary>
    /// Proof that the re-key is wired into <c>LoadCycle</c>, not merely available.
    ///
    /// <para>A ballot reaches storage under whatever casing the caller carried — the
    /// "view as" header used to hand back "mom" verbatim — and a comparer does not
    /// survive JSON, so a later lookup keyed on "Mom" misses it. The write paths hide
    /// this, because they rebuild the dictionary case-insensitively before asking
    /// anything of it; the <i>read</i> paths are where it bites. Here Mom is refused
    /// scheduling for not having finished a ballot she completed.</para>
    /// </summary>
    [Fact]
    public async Task ABallotStoredUnderAnotherCasingStillUnlocksScheduling()
    {
        Seed(Cycle(movies: [1, 2], votes: new()
        {
            ["mom"] = new() { [1] = "Up", [2] = "Up" }
        }));

        var act = async () => await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        await act.Should().NotThrowAsync(
            "she has voted on every movie in the draw; the casing of the key is not her problem");
        Stored().Schedule!.ProposedBy.Should().Be(Mom);
    }

    [Fact]
    public async Task AnUnrecognisedVoteIsRejected()
    {
        Seed(Cycle());

        var act = async () => await _svc.CastVoteAsync(Mom, 1, "Maybe", isTest: true);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// A movie outside this week's draw. Accepting it would let a ballot name a
    /// film nobody was offered, and <c>EveryoneVoted</c> counts ballots against the
    /// draw — so the extra entry would sit there forever.
    /// </summary>
    [Fact]
    public async Task AVoteOnAMovieOutsideThisWeeksDrawIsRejected()
    {
        Seed(Cycle(movies: [1, 2]));

        var act = async () => await _svc.CastVoteAsync(Mom, 99, "Up", isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*this week's drawn movies*");
    }

    [Theory]
    [InlineData("Resolved")]
    [InlineData("NoMatch")]
    [InlineData("Cancelled")]
    public async Task VotingIsClosedOnceTheWeekHasFinished(string status)
    {
        Seed(Cycle(status: status, resolvedMovieId: status == "Resolved" ? 1 : null));

        var act = async () => await _svc.CastVoteAsync(Mom, 1, "Up", isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already*");
    }

    // ------------------------------------------ votes and the permanent lists

    /// <summary>
    /// "Never" is the permanent one — it retires the film from every future draw,
    /// so it has to be written the moment it is cast rather than at resolution.
    /// </summary>
    [Fact]
    public async Task ANeverVoteRetiresTheMovieImmediately()
    {
        Seed(Cycle());

        await _svc.CastVoteAsync(Mom, 1, "Never", isTest: true);

        var entry = StoredLists()[1];
        entry.NeverShowAgain.Should().BeTrue();
        entry.NeverShowAgainUtc.Should().NotBeNull();
    }

    /// <summary>
    /// The correction case, and the reason the flags are reconciled from the
    /// current ballots rather than only ever set. Without this, one mis-tap retires
    /// a film permanently even after the visible vote has been changed back — and
    /// nothing in the UI would show why it never appears again.
    /// </summary>
    [Fact]
    public async Task ChangingANeverVoteBackUnretiresTheMovie()
    {
        Seed(Cycle());

        await _svc.CastVoteAsync(Mom, 1, "Never", isTest: true);
        await _svc.CastVoteAsync(Mom, 1, "Up", isTest: true);

        StoredLists()[1].NeverShowAgain.Should().BeFalse(
            "the visible vote changed, so the exclusion it caused must change with it");
    }

    /// <summary>A "Down" starts the cooling-off window rather than retiring the film.</summary>
    [Fact]
    public async Task ADownVoteStartsTheCoolingOffWindow()
    {
        Seed(Cycle());

        await _svc.CastVoteAsync(Mom, 1, "Down", isTest: true);

        var entry = StoredLists()[1];
        entry.LastDisagreedUtc.Should().NotBeNull();
        entry.NeverShowAgain.Should().BeFalse("a Down is not a Never");
    }

    [Fact]
    public async Task ChangingADownVoteBackClearsTheCoolingOffWindow()
    {
        Seed(Cycle());

        await _svc.CastVoteAsync(Mom, 1, "Down", isTest: true);
        await _svc.CastVoteAsync(Mom, 1, "Up", isTest: true);

        StoredLists()[1].LastDisagreedUtc.Should().BeNull();
    }

    /// <summary>
    /// One person's Never is enough — the film is out regardless of what the other
    /// thinks of it.
    /// </summary>
    [Fact]
    public async Task OneNeverIsEnoughEvenIfTheOtherPersonLikedIt()
    {
        Seed(Cycle());

        await _svc.CastVoteAsync(Dad, 1, "Up", isTest: true);
        await _svc.CastVoteAsync(Mom, 1, "Never", isTest: true);

        StoredLists()[1].NeverShowAgain.Should().BeTrue();
    }

    // ----------------------------------------------------------- resolution

    /// <summary>
    /// The week resolves the instant the last ballot lands rather than waiting for
    /// Sunday. Anything else would leave a decided week looking undecided for days.
    /// </summary>
    [Fact]
    public async Task TheWeekResolvesOnTheLastVoteNotAtTheDeadline()
    {
        Seed(Cycle(movies: [1, 2], votes: Ballots(
            (Mom, [(1, "Up"), (2, "Down")]),
            (Dad, [(1, "Up")]))));

        var cycle = await _svc.CastVoteAsync(Dad, 2, "Up", isTest: true);

        cycle.Status.Should().Be("Resolved");
        cycle.ResolvedMovieId.Should().Be(1, "movie 1 is the only one both said Up to");
        cycle.ResolvedUtc.Should().NotBeNull();
    }

    /// <summary>
    /// Only a mutual "Up" counts. A film one person merely did not veto is not one
    /// they agreed to watch.
    /// </summary>
    [Fact]
    public async Task AWeekWithNoMutualApprovalIsANoMatch()
    {
        Seed(Cycle(movies: [1, 2], votes: Ballots(
            (Mom, [(1, "Up"), (2, "Down")]),
            (Dad, [(1, "Down")]))));

        var cycle = await _svc.CastVoteAsync(Dad, 2, "Up", isTest: true);

        cycle.Status.Should().Be("NoMatch");
        cycle.ResolvedMovieId.Should().BeNull();
    }

    /// <summary>
    /// When several films are mutually approved, one is picked and the rest are
    /// recorded as approved-but-not-picked. That record is informational, but it is
    /// the only trace that the household agreed on them.
    /// </summary>
    [Fact]
    public async Task TheRunnersUpAreRecordedAsApproved()
    {
        Seed(Cycle(movies: [1, 2], votes: Ballots(
            (Mom, [(1, "Up"), (2, "Up")]),
            (Dad, [(1, "Up")]))));

        var cycle = await _svc.CastVoteAsync(Dad, 2, "Up", isTest: true);

        cycle.ResolvedMovieId.Should().BeOneOf(1, 2);
        var runnerUp = cycle.ResolvedMovieId == 1 ? 2 : 1;
        StoredLists()[runnerUp].LastApprovedUtc.Should().NotBeNull();
        StoredLists().Should().NotContainKey(cycle.ResolvedMovieId!.Value,
            "the picked film is not a runner-up");
    }

    /// <summary>
    /// A resolved week keeps any handshake that was already under way. The first
    /// person to finish their ballot can propose times while the other is still
    /// voting, and resetting that on resolution would silently discard it.
    /// </summary>
    [Fact]
    public async Task ResolutionPreservesAProposalMadeWhileVotingWasStillOpen()
    {
        var schedule = DateNightPolicy.NewSchedule() with
        {
            Status = "AwaitingApproval",
            ProposedBy = Mom,
            ProposedSlots = [Slot()],
            AcknowledgedBy = [Mom]
        };

        Seed(Cycle(movies: [1], votes: Ballots((Mom, [(1, "Up")])), schedule: schedule));

        var cycle = await _svc.CastVoteAsync(Dad, 1, "Up", isTest: true);

        cycle.Status.Should().Be("Resolved");
        cycle.Schedule!.Status.Should().Be("AwaitingApproval");
        cycle.Schedule.ProposedBy.Should().Be(Mom);
    }

    // ---------------------------------------------------- propose / approve

    private WeeklyCycle ResolvedWeek(ScheduleState? schedule = null) =>
        Cycle(movies: [1],
              status: "Resolved",
              votes: Ballots((Mom, [(1, "Up")]), (Dad, [(1, "Up")])),
              resolvedMovieId: 1,
              schedule: schedule);

    /// <summary>
    /// Proposing times is gated on finishing your own ballot. Otherwise the flyer's
    /// whole sequence — vote, then agree a time — can be skipped.
    /// </summary>
    [Fact]
    public async Task TimesCannotBeProposedBeforeYourBallotIsComplete()
    {
        Seed(Cycle(movies: [1, 2], votes: Ballots((Mom, [(1, "Up")]))));

        var act = async () => await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Finish voting*");
    }

    [Fact]
    public async Task TheAdminCannotProposeTimes()
    {
        Seed(ResolvedWeek());

        var act = async () => await _svc.ProposeScheduleAsync(Paul, [Slot()], isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The handshake alternates. Letting the proposer propose again would let one
    /// person keep replacing the offer the other is still looking at.
    /// </summary>
    [Fact]
    public async Task TheProposerCannotProposeAgainWhileWaitingOnTheOtherPerson()
    {
        Seed(ResolvedWeek());
        await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        var act = async () => await _svc.ProposeScheduleAsync(Mom, [Slot(daysAhead: 4)], isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not your turn*");
    }

    /// <summary>
    /// A counter-offer is one concrete alternative, not a fresh menu — otherwise the
    /// negotiation never narrows.
    /// </summary>
    [Fact]
    public async Task ACounterProposalMustNameExactlyOneSlot()
    {
        Seed(ResolvedWeek());
        await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        var act = async () => await _svc.ProposeScheduleAsync(
            Dad, [Slot(daysAhead: 4), Slot(daysAhead: 5)], isTest: true);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*exactly one*");
    }

    /// <summary>Countering hands the turn back, so the original proposer now approves.</summary>
    [Fact]
    public async Task ACounterProposalFlipsWhoIsWaitingOnWhom()
    {
        Seed(ResolvedWeek());
        await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        var cycle = await _svc.ProposeScheduleAsync(Dad, [Slot(daysAhead: 4)], isTest: true);

        cycle.Schedule!.ProposedBy.Should().Be(Dad);
        cycle.Schedule.Status.Should().Be("AwaitingApproval");
        cycle.Schedule.AcknowledgedBy.Should().BeEquivalentTo([Dad],
            "the counter is new to Mom, so her acknowledgement does not carry over");
    }

    /// <summary>Agreeing with yourself is not agreement.</summary>
    [Fact]
    public async Task TheProposerCannotApproveTheirOwnProposal()
    {
        Seed(ResolvedWeek());
        var slot = Slot();
        await _svc.ProposeScheduleAsync(Mom, [slot], isTest: true);

        var act = async () => await _svc.ApproveScheduleAsync(Mom, slot, isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*you proposed*");
    }

    [Fact]
    public async Task ASlotThatWasNeverOfferedCannotBeApproved()
    {
        Seed(ResolvedWeek());
        await _svc.ProposeScheduleAsync(Mom, [Slot()], isTest: true);

        var act = async () => await _svc.ApproveScheduleAsync(Dad, Slot(daysAhead: 5), isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*wasn't one of the proposed*");
    }

    /// <summary>
    /// A proposal can sit unanswered long enough to go stale. Locking in a showtime
    /// that has already happened would start a download for a date night nobody can
    /// attend.
    /// </summary>
    [Fact]
    public async Task AShowtimeThatHasAlreadyPassedCannotBeApproved()
    {
        var past = new ProposedSlot(
            DateNightPolicy.HawaiiDate(DateTime.UtcNow).AddDays(-2).ToString("yyyy-MM-dd"), "19:00");

        Seed(ResolvedWeek(DateNightPolicy.NewSchedule() with
        {
            Status = "AwaitingApproval",
            ProposedBy = Mom,
            ProposedSlots = [past],
            AcknowledgedBy = [Mom]
        }));

        var act = async () => await _svc.ApproveScheduleAsync(Dad, past, isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already passed*");
    }

    /// <summary>
    /// Approval is what locks the week in and is the only thing that starts a
    /// download — nothing downloads before both people have agreed a time.
    /// </summary>
    [Fact]
    public async Task ApprovingLocksTheShowtimeAndStartsTheDownload()
    {
        Seed(ResolvedWeek());
        var slot = Slot();
        await _svc.ProposeScheduleAsync(Mom, [slot], isTest: true);

        var cycle = await _svc.ApproveScheduleAsync(Dad, slot, isTest: true);

        cycle.Schedule!.Status.Should().Be("Locked");
        cycle.Schedule.LockedSlot.Should().Be(slot);
        cycle.Schedule.LockedUtc.Should().NotBeNull();
    }

    /// <summary>
    /// The lock-in is persisted before the Radarr call and outside its failure
    /// path. Mom and Dad agreed on a time; an unreachable download client must not
    /// quietly undo that — it is recorded as a failed download against a showtime
    /// that still stands, which is what the retry button is for.
    /// </summary>
    [Fact]
    public async Task AnUnreachableRadarrDoesNotUndoTheAgreedShowtime()
    {
        Seed(ResolvedWeek());
        var slot = Slot();
        await _svc.ProposeScheduleAsync(Mom, [slot], isTest: true);

        var cycle = await _svc.ApproveScheduleAsync(Dad, slot, isTest: true);

        cycle.Schedule!.Status.Should().Be("Locked");
        cycle.Schedule.DownloadStatus.Should().Be("Failed");
        cycle.Schedule.DownloadMessage.Should().NotBeNullOrWhiteSpace(
            "the reason has to reach the screen or the retry button is a guess");
        Stored().Schedule!.Status.Should().Be("Locked", "and it survived to storage");
    }

    /// <summary>
    /// The double-tap. A browser retry can arrive while the first request is still
    /// waiting on Radarr; confirming the identical slot must be a no-op rather than
    /// a conflict, and must not trigger a second grab of the same film.
    /// </summary>
    [Fact]
    public async Task ConfirmingTheSameSlotTwiceIsANoOp()
    {
        Seed(ResolvedWeek());
        var slot = Slot();
        await _svc.ProposeScheduleAsync(Mom, [slot], isTest: true);
        var first = await _svc.ApproveScheduleAsync(Dad, slot, isTest: true);

        var second = await _svc.ApproveScheduleAsync(Dad, slot, isTest: true);

        second.Schedule!.Status.Should().Be("Locked");
        second.Schedule.LockedUtc.Should().Be(first.Schedule!.LockedUtc,
            "a second confirmation of the same slot must change nothing at all");
    }

    /// <summary>
    /// A showtime cannot be locked before the film is decided — there would be
    /// nothing to download.
    /// </summary>
    [Fact]
    public async Task AShowtimeCannotBeConfirmedBeforeTheWeekHasResolved()
    {
        Seed(Cycle(movies: [1], votes: Ballots((Mom, [(1, "Up")])), schedule: DateNightPolicy.NewSchedule() with
        {
            Status = "AwaitingApproval",
            ProposedBy = Mom,
            ProposedSlots = [Slot()],
            AcknowledgedBy = [Mom]
        }));

        var act = async () => await _svc.ApproveScheduleAsync(Dad, Slot(), isTest: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ballots must be complete*");
    }
}
