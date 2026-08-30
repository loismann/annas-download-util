using System.Text.Json;
using System.Text.Json.Nodes;
using AnnasArchive.API.Data;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The judgement behind "could we actually get this film?", and the per-person
/// announcement record.
///
/// <para>392 lines with no test naming it. The important half is
/// <see cref="DateNightAvailabilityService.IsObtainable"/> — a public static used
/// by <i>both</i> the availability scan and the grab at schedule lock-in, so a
/// wrong answer either reports a perfectly gettable film as unavailable, or picks
/// a release Radarr already told us it did not want.</para>
///
/// <para>The scan loop itself is not covered here: it is a multi-hour paced walk
/// over live indexers, and the part of it worth pinning is the decision it makes
/// on each release, which is exactly what <c>IsObtainable</c> is.</para>
/// </summary>
public sealed class DateNightAvailabilityServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "datenight-avail-tests", Guid.NewGuid().ToString("N"));

    private readonly AppDatabase _db;
    private readonly DateNightAvailabilityService _svc;

    public DateNightAvailabilityServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "app.db")
            })
            .Build();

        _db = new AppDatabase(config);

        // The scope factory is only reached by the scan and the pool listing, which
        // these do not exercise. Throwing rather than stubbing means any future
        // change that reaches Radarr from a path tested here fails loudly.
        _svc = new DateNightAvailabilityService(new UnreachableRadarr(), _db);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private sealed class UnreachableRadarr : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Radarr is not part of these tests");
    }

    // ----------------------------------------------------------- IsObtainable

    /// <summary>A release with reasons attached, as Radarr's interactive search returns it.</summary>
    private static JsonObject Release(bool rejected, params string[] rejections)
    {
        var obj = new JsonObject { ["rejected"] = rejected };
        if (rejections.Length > 0)
            obj["rejections"] = new JsonArray(rejections.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return obj;
    }

    /// <summary>The simple case: Radarr would take it.</summary>
    [Fact]
    public void AReleaseRadarrWouldGrabIsObtainable()
    {
        DateNightAvailabilityService.IsObtainable(Release(rejected: false)).Should().BeTrue();
    }

    /// <summary>
    /// The whole reason this method exists. For a film already on the server Radarr
    /// rejects <i>every</i> release as redundant, so counting "not rejected" would
    /// report a movie you already own as impossible to get — and the pool would
    /// quietly stop offering the films most likely to work.
    /// </summary>
    [Theory]
    [InlineData("already meets cutoff")]
    [InlineData("Existing file on disk")]
    [InlineData("already imported")]
    [InlineData("already been grabbed")]
    public void ARejectionThatOnlyMeansWeAlreadyHaveItIsStillObtainable(string reason)
    {
        DateNightAvailabilityService.IsObtainable(Release(true, reason)).Should().BeTrue();
    }

    /// <summary>
    /// A genuine objection to the copy itself. This is the half that must stay
    /// rejected, or lock-in grabs a release Radarr has already refused.
    /// </summary>
    [Theory]
    [InlineData("x265 is not wanted")]
    [InlineData("Unable to parse release")]
    [InlineData("Quality Bluray-2160p is not wanted")]
    [InlineData("Release contains no video")]
    public void ARealObjectionToTheReleaseIsNotObtainable(string reason)
    {
        DateNightAvailabilityService.IsObtainable(Release(true, reason)).Should().BeFalse();
    }

    /// <summary>
    /// Every reason has to be a redundancy reason. One genuine objection alongside
    /// a redundant one means the copy itself is unacceptable — an "any" test here
    /// would wave through exactly the releases Radarr refuses.
    /// </summary>
    [Fact]
    public void OneRealObjectionOutweighsAnyNumberOfRedundancyReasons()
    {
        var release = Release(true,
            "Existing file on disk", "already imported", "x265 is not wanted");

        DateNightAvailabilityService.IsObtainable(release).Should().BeFalse();
    }

    /// <summary>All-redundant with several reasons is still obtainable.</summary>
    [Fact]
    public void SeveralRedundancyReasonsTogetherAreStillObtainable()
    {
        var release = Release(true, "Existing file on disk", "already meets cutoff");

        DateNightAvailabilityService.IsObtainable(release).Should().BeTrue();
    }

    /// <summary>
    /// Radarr writes these reasons as prose around the phrase, and its casing is
    /// not stable across versions. Matching exactly would silently turn every
    /// already-owned film unavailable after an upgrade.
    /// </summary>
    [Theory]
    [InlineData("EXISTING FILE ON DISK")]
    [InlineData("Not upgrading: already meets cutoff for this profile")]
    [InlineData("This release has ALREADY BEEN GRABBED by another client")]
    public void TheReasonIsMatchedLooselyBecauseRadarrPhrasesItDifferently(string reason)
    {
        DateNightAvailabilityService.IsObtainable(Release(true, reason)).Should().BeTrue();
    }

    /// <summary>
    /// Rejected with nothing saying why. Unexplained is not the same as redundant,
    /// and guessing in favour would report films as gettable that are not.
    /// </summary>
    [Fact]
    public void ARejectionWithNoStatedReasonIsNotObtainable()
    {
        DateNightAvailabilityService.IsObtainable(Release(true)).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyRejectionListIsNotObtainable()
    {
        var release = new JsonObject { ["rejected"] = true, ["rejections"] = new JsonArray() };

        DateNightAvailabilityService.IsObtainable(release).Should().BeFalse();
    }

    /// <summary>
    /// A release document with no <c>rejected</c> field at all — Radarr always
    /// sends one, so this pins what happens if that ever stops being true.
    /// </summary>
    [Fact]
    public void AReleaseThatNeverMentionsRejectionCountsAsObtainable()
    {
        DateNightAvailabilityService.IsObtainable(new JsonObject { ["title"] = "Some Rip" })
            .Should().BeTrue();
    }

    // ------------------------------------------------------- announcements

    /// <summary>
    /// Being shown the announcement is not the same as acknowledging it — someone
    /// who closes the tab has to get another chance, or a single unlucky page load
    /// consumes their one showing.
    /// </summary>
    [Fact]
    public void BeingShownTheAnnouncementDoesNotCountAsHavingSeenIt()
    {
        _svc.RecordAnnouncementShown("Mom");

        _svc.HasSeenAnnouncement("Mom").Should().BeFalse();
    }

    [Fact]
    public void DismissingTheAnnouncementCountsAsHavingSeenIt()
    {
        _svc.MarkAnnouncementSeen("Mom");

        _svc.HasSeenAnnouncement("Mom").Should().BeTrue();
    }

    [Fact]
    public void SomeoneWhoHasNeverLoadedAPageHasNotSeenIt()
    {
        _svc.HasSeenAnnouncement("Dad").Should().BeFalse();
    }

    /// <summary>
    /// The recorded time is first sighting, not most recent render — the question
    /// it answers is when they first met it.
    /// </summary>
    [Fact]
    public async Task TheShownTimeIsTheFirstSightingNotTheLatestRender()
    {
        _svc.RecordAnnouncementShown("Mom");
        var first = _svc.GetAnnouncementStatus().Single(r => r.Person == "Mom").ShownUtc;

        await Task.Delay(15);
        _svc.RecordAnnouncementShown("Mom");

        _svc.GetAnnouncementStatus().Single(r => r.Person == "Mom").ShownUtc.Should().Be(first);
    }

    /// <summary>
    /// A dismissal with no recorded showing — a replayed request, or a dismissal
    /// that raced the record. The row must not be left half-empty, because the
    /// admin view reads both fields to tell "never saw it" from "saw and ignored".
    /// </summary>
    [Fact]
    public void DismissingWithoutARecordedShowingBackfillsTheShownTime()
    {
        _svc.MarkAnnouncementSeen("Dad");

        var row = _svc.GetAnnouncementStatus().Single(r => r.Person == "Dad");
        row.ShownUtc.Should().NotBeNull();
        row.DismissedUtc.Should().NotBeNull();
    }

    /// <summary>A second dismissal must not move the original timestamp.</summary>
    [Fact]
    public async Task DismissingTwiceKeepsTheFirstDismissalTime()
    {
        _svc.MarkAnnouncementSeen("Mom");
        var first = _svc.GetAnnouncementStatus().Single(r => r.Person == "Mom").DismissedUtc;

        await Task.Delay(15);
        _svc.MarkAnnouncementSeen("Mom");

        _svc.GetAnnouncementStatus().Single(r => r.Person == "Mom").DismissedUtc.Should().Be(first);
    }

    /// <summary>
    /// The recovery path: logging into Dad's account to check something runs the
    /// same code a real Dad would and burns his one genuine showing.
    /// </summary>
    [Fact]
    public void ResettingClearsBothHalvesOfThePersonsRecord()
    {
        _svc.MarkAnnouncementSeen("Dad");

        _svc.ResetAnnouncement("Dad");

        _svc.HasSeenAnnouncement("Dad").Should().BeFalse();
        var row = _svc.GetAnnouncementStatus().Single(r => r.Person == "Dad");
        row.ShownUtc.Should().BeNull();
        row.DismissedUtc.Should().BeNull();
    }

    [Fact]
    public void ResettingSomeoneWithNoRecordIsHarmless()
    {
        var act = () => _svc.ResetAnnouncement("Mom");

        act.Should().NotThrow();
        _svc.HasSeenAnnouncement("Mom").Should().BeFalse();
    }

    /// <summary>
    /// The announcement is for the audience. Paul runs the thing and would only
    /// clutter the admin view with a row that never means anything.
    /// </summary>
    [Fact]
    public void TheAdminIsNotAnAnnouncementRecipient()
    {
        _svc.GetAnnouncementStatus().Select(r => r.Person)
            .Should().BeEquivalentTo("Mom", "Dad");
    }

    /// <summary>
    /// Names survive storage case-insensitively, because this state is rebuilt with
    /// an <c>OrdinalIgnoreCase</c> comparer after every load rather than relying on
    /// the one the writer used — a comparer does not survive JSON. The Date Night
    /// ballot dictionary does <i>not</i> do this, which is the trap recorded in
    /// <c>ASSERTIONS_AND_ASSUMPTIONS.md</c>; this test is here so the working
    /// version cannot quietly regress to match the broken one.
    /// </summary>
    [Fact]
    public void AnnouncementNamesAreMatchedCaseInsensitivelyAcrossAReload()
    {
        _svc.MarkAnnouncementSeen("mom");

        _svc.HasSeenAnnouncement("Mom").Should().BeTrue(
            "the state is rebuilt case-insensitively on load, so the stored casing does not matter");
    }

    // --------------------------------------------------------- scan status

    [Fact]
    public void WithNoScanEverRunTheStatusIsIdleAndEmpty()
    {
        var status = _svc.GetScanStatus();

        status.Running.Should().BeFalse();
        status.Checked.Should().Be(0);
        status.Total.Should().Be(0);
        status.Error.Should().BeNull();
    }

    /// <summary>
    /// A scan killed by a container restart leaves "Running: true" in the database
    /// forever. Trusting that flag would block every future scan permanently, so it
    /// is reconciled against whether this process is actually scanning.
    /// </summary>
    [Fact]
    public void AScanInterruptedByARestartReadsAsFinishedWithANoteNotAsStillRunning()
    {
        _db.SetState("date-night:availability-scan", JsonSerializer.Serialize(
            new AvailabilityScanStatus(true, 12, 300, DateTime.UtcNow.AddHours(-2), null, null), Json));

        var status = _svc.GetScanStatus();

        status.Running.Should().BeFalse();
        status.Error.Should().Contain("Interrupted");
        status.Checked.Should().Be(12, "the work it did before dying still counts");
    }

    /// <summary>An error already recorded is more specific than the generic note, so it wins.</summary>
    [Fact]
    public void AnAlreadyRecordedErrorIsNotOverwrittenByTheInterruptedNote()
    {
        _db.SetState("date-night:availability-scan", JsonSerializer.Serialize(
            new AvailabilityScanStatus(true, 5, 300, DateTime.UtcNow, null, "Radarr refused the search"), Json));

        _svc.GetScanStatus().Error.Should().Be("Radarr refused the search");
    }

    /// <summary>
    /// Unreadable state must not take the page down with it — the scan status is
    /// rendered on a page that has plenty else to show.
    /// </summary>
    [Fact]
    public void UnreadableScanStateFallsBackToIdleRatherThanThrowing()
    {
        _db.SetState("date-night:availability-scan", "{ this is not json");

        var act = () => _svc.GetScanStatus();

        act.Should().NotThrow();
        act().Running.Should().BeFalse();
    }

    [Fact]
    public void UnreadableAvailabilityStateFallsBackToEmptyRatherThanThrowing()
    {
        _db.SetState("date-night:availability", "not json at all");

        _svc.GetAvailability().Should().BeEmpty();
    }

    /// <summary>Stored results round-trip, including the derived availability flag.</summary>
    [Fact]
    public void StoredResultsRoundTripAndAGrabbableCountMeansAvailable()
    {
        _db.SetState("date-night:availability", JsonSerializer.Serialize(
            new Dictionary<int, PoolAvailability>
            {
                [7] = new(7, "The Blob", 1958, Grabbable: 3, RejectedOnly: 1, DateTime.UtcNow),
                [8] = new(8, "Nothing Here", 1960, Grabbable: 0, RejectedOnly: 9, DateTime.UtcNow)
            }, Json));

        var availability = _svc.GetAvailability();

        availability[7].IsAvailable.Should().BeTrue();
        availability[7].Title.Should().Be("The Blob");
        availability[8].IsAvailable.Should().BeFalse("nothing grabbable means nothing to watch");
    }
}
