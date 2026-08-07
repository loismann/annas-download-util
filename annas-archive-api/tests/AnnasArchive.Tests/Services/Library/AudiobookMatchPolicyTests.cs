using AnnasArchive.API.Services.Library;
using FluentAssertions;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// The decisions standing between a wrong guess and <c>Directory.Move</c> on
/// somebody's audiobook library.
///
/// <para>These rules had no test at all, and the one live bug they exist to stop
/// has already happened once: a Warhammer 40k novel called <em>Dead in the
/// Water</em> was confidently replaced with Stuart Woods' unrelated same-titled
/// book. The service around them is a filesystem walk plus three remote calls,
/// which is why it stayed untested — so the rules were lifted out where they can
/// be asked directly.</para>
/// </summary>
public sealed class AudiobookMatchPolicyTests
{
    private static AudiobookMatch Match(string source, double confidence) =>
        new("Dead in the Water", "Sandy Mitchell", 2008, source, confidence);

    // ── Which bar applies ────────────────────────────────────────────────

    /// <summary>
    /// The two catalogue sources are scored against a real search hit, so their
    /// confidence is measured and the lower bar is warranted.
    /// </summary>
    [Theory]
    [InlineData(AudiobookMatchPolicy.OpenLibrarySource)]
    [InlineData(AudiobookMatchPolicy.GoogleBooksSource)]
    public void ScoredCatalogueSourcesGetTheLowerBar(string source) =>
        AudiobookMatchPolicy.RequiredConfidence(source)
            .Should().Be(AudiobookMatchPolicy.CatalogueConfidence);

    /// <summary>
    /// The model's confidence is self-reported with nothing corroborating it.
    /// Same number from GPT and from OpenLibrary does not mean the same thing.
    /// </summary>
    [Fact]
    public void TheModelGetsTheStricterBar() =>
        AudiobookMatchPolicy.RequiredConfidence(AudiobookMatchPolicy.AiSource)
            .Should().Be(AudiobookMatchPolicy.AiConfidence);

    /// <summary>
    /// The safe default, and the reason this is an allowlist rather than a check
    /// for <c>== "AI"</c>. A source string nobody recognised — a sidecar written
    /// before <c>matchSource</c> existed, a hand edit, a future provider, a typo
    /// in the string literal — must not fall through to the permissive bar.
    /// Under the old <c>source == "AI" ? 0.9 : 0.75</c> rule, every one of those
    /// did.
    /// </summary>
    [Theory]
    [InlineData("cached")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("openlibrary")]   // wrong case is not the known source
    [InlineData("OpenLibary")]    // a typo in the literal must not relax the bar
    [InlineData("Audnexus")]      // a provider added later, before anyone calibrates it
    public void AnUnrecognisedSourceGetsTheStricterBar(string? source) =>
        AudiobookMatchPolicy.RequiredConfidence(source)
            .Should().Be(AudiobookMatchPolicy.AiConfidence);

    /// <summary>The whole point of two bars: they must not be the same number.</summary>
    [Fact]
    public void TheTwoBarsAreActuallyDifferent() =>
        AudiobookMatchPolicy.AiConfidence.Should().BeGreaterThan(AudiobookMatchPolicy.CatalogueConfidence);

    // ── Is this match good enough to act on ──────────────────────────────

    /// <summary>
    /// The observed failure, pinned. GPT-4o only ever returns 0.8 or 0.9 in
    /// practice; the wrong matches clustered at 0.8. An 0.8 from the model is
    /// rejected — and the same 0.8 from a catalogue is accepted, which is the
    /// distinction the whole policy exists to make.
    /// </summary>
    [Fact]
    public void AnEightTenthsGuessFromTheModelIsRejected() =>
        AudiobookMatchPolicy.IsTrusted(Match(AudiobookMatchPolicy.AiSource, 0.8)).Should().BeFalse();

    [Fact]
    public void TheSameEightTenthsFromACatalogueIsAccepted() =>
        AudiobookMatchPolicy.IsTrusted(Match(AudiobookMatchPolicy.OpenLibrarySource, 0.8)).Should().BeTrue();

    [Fact]
    public void AConfidentModelAnswerIsAccepted() =>
        AudiobookMatchPolicy.IsTrusted(Match(AudiobookMatchPolicy.AiSource, 0.9)).Should().BeTrue();

    /// <summary>Both bars are inclusive — exactly at the threshold passes.</summary>
    [Theory]
    [InlineData(AudiobookMatchPolicy.OpenLibrarySource, AudiobookMatchPolicy.CatalogueConfidence)]
    [InlineData(AudiobookMatchPolicy.AiSource, AudiobookMatchPolicy.AiConfidence)]
    public void ExactlyAtTheBarCounts(string source, double bar) =>
        AudiobookMatchPolicy.IsTrusted(Match(source, bar)).Should().BeTrue();

    /// <summary>And just under it does not.</summary>
    [Theory]
    [InlineData(AudiobookMatchPolicy.OpenLibrarySource, 0.749)]
    [InlineData(AudiobookMatchPolicy.AiSource, 0.899)]
    public void JustUnderTheBarDoesNot(string source, double confidence) =>
        AudiobookMatchPolicy.IsTrusted(Match(source, confidence)).Should().BeFalse();

    [Fact]
    public void NoMatchIsNeverTrusted() =>
        AudiobookMatchPolicy.IsTrusted(null).Should().BeFalse();

    // ── Picking between the free sources ─────────────────────────────────

    /// <summary>
    /// Both sources answer the same question on the same scale, so the higher
    /// confidence wins outright — not whichever was queried first.
    /// </summary>
    [Fact]
    public void TheHigherConfidenceWinsRegardlessOfArgumentOrder()
    {
        var weak = Match(AudiobookMatchPolicy.OpenLibrarySource, 0.78);
        var strong = Match(AudiobookMatchPolicy.GoogleBooksSource, 0.93);

        AudiobookMatchPolicy.BestOf(weak, strong).Should().BeSameAs(strong);
        AudiobookMatchPolicy.BestOf(strong, weak).Should().BeSameAs(strong);
    }

    /// <summary>
    /// A winner still has to clear its own bar. "Best available" is not the same
    /// as "good enough", and conflating them is how a folder gets renamed after
    /// every source shrugged.
    /// </summary>
    [Fact]
    public void TheBestOfTwoPoorAnswersIsStillRejected() =>
        AudiobookMatchPolicy.BestOf(
            Match(AudiobookMatchPolicy.OpenLibrarySource, 0.4),
            Match(AudiobookMatchPolicy.GoogleBooksSource, 0.6))
            .Should().BeNull();

    [Fact]
    public void ASingleGoodAnswerSurvivesTheOtherSourceFailing() =>
        AudiobookMatchPolicy.BestOf(null, Match(AudiobookMatchPolicy.GoogleBooksSource, 0.8))
            .Should().NotBeNull();

    [Fact]
    public void NothingFromAnySourceIsNoMatch() =>
        AudiobookMatchPolicy.BestOf(null, null).Should().BeNull();

    /// <summary>
    /// The bar is applied per-source, not as one hardcoded number. The service
    /// previously compared the winner against the catalogue threshold directly,
    /// so a model-sourced answer reaching this path would have been waved
    /// through at 0.8.
    /// </summary>
    [Fact]
    public void TheWinnersOwnBarIsApplied_NotTheCatalogueOne() =>
        AudiobookMatchPolicy.BestOf(Match(AudiobookMatchPolicy.AiSource, 0.8)).Should().BeNull();

    // ── Reusing a match cached in a sidecar ──────────────────────────────

    /// <summary>
    /// A cached match is re-checked against today's rules, not the rules in force
    /// when it was written. Otherwise raising a threshold would silently exempt
    /// every folder already on disk — the exact population most likely to hold a
    /// bad match, since they were matched under the looser rule.
    /// </summary>
    [Fact]
    public void ACachedMatchThatNoLongerClearsTodaysBarIsRecomputed() =>
        AudiobookMatchPolicy.CanReuseCachedMatch(Match(AudiobookMatchPolicy.AiSource, 0.8))
            .Should().BeFalse();

    [Fact]
    public void ACachedMatchThatStillClearsItIsReused() =>
        AudiobookMatchPolicy.CanReuseCachedMatch(Match(AudiobookMatchPolicy.OpenLibrarySource, 0.9))
            .Should().BeTrue();

    /// <summary>
    /// The reuse rule is exactly the acceptance rule. If these two ever drift, a
    /// match becomes acceptable only depending on whether it happens to be on
    /// disk yet, which is not a property of the book.
    /// </summary>
    [Theory]
    [InlineData(AudiobookMatchPolicy.OpenLibrarySource, 0.74)]
    [InlineData(AudiobookMatchPolicy.OpenLibrarySource, 0.75)]
    [InlineData(AudiobookMatchPolicy.AiSource, 0.89)]
    [InlineData(AudiobookMatchPolicy.AiSource, 0.9)]
    [InlineData("cached", 0.85)]
    public void ReuseAndAcceptanceAreTheSameRule(string source, double confidence) =>
        AudiobookMatchPolicy.CanReuseCachedMatch(Match(source, confidence))
            .Should().Be(AudiobookMatchPolicy.IsTrusted(Match(source, confidence)));

    // ── Whether to spend money ───────────────────────────────────────────

    [Fact]
    public void TheModelIsAskedOnlyWhenTheFreeSourcesFailed() =>
        AudiobookMatchPolicy.ShouldTryAi(null, aiAttempts: 0, "Dead in the Water").Should().BeTrue();

    /// <summary>A usable free answer means there is nothing to pay for.</summary>
    [Fact]
    public void TheModelIsNotAskedWhenAFreeSourceAlreadyAnswered() =>
        AudiobookMatchPolicy.ShouldTryAi(
            Match(AudiobookMatchPolicy.OpenLibrarySource, 0.9), aiAttempts: 0, "Dead in the Water")
            .Should().BeFalse();

    /// <summary>
    /// A rejected free answer is not an answer. The service reaches this decision
    /// holding whatever <c>BestOf</c> returned, so the check has to be "is it
    /// trusted", not "is it null" — otherwise a 0.4 match that was already
    /// discarded would block the fallback that exists for exactly that case.
    /// </summary>
    [Fact]
    public void ARejectedFreeAnswerStillTriggersTheFallback() =>
        AudiobookMatchPolicy.ShouldTryAi(
            Match(AudiobookMatchPolicy.OpenLibrarySource, 0.4), aiAttempts: 0, "Dead in the Water")
            .Should().BeTrue();

    /// <summary>
    /// The budget is per folder and permanent — it is read from the sidecar, so
    /// it survives restarts. A folder cannot cost more than three paid lookups
    /// however many times it is scanned.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(9, false)]
    public void ThePaidLookupBudgetIsEnforced(int attemptsSoFar, bool expected) =>
        AudiobookMatchPolicy.ShouldTryAi(null, attemptsSoFar, "Dead in the Water").Should().Be(expected);

    /// <summary>
    /// With no parsed title there is no question to ask, only an invitation to
    /// invent an answer — which is how a confident wrong match gets made.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheModelIsNotAskedAboutAFolderWithNoParsedTitle(string? title) =>
        AudiobookMatchPolicy.ShouldTryAi(null, aiAttempts: 0, title).Should().BeFalse();

    // ── Giving up ────────────────────────────────────────────────────────

    /// <summary>
    /// Counts the attempt that just failed, so the fifth failure is the one that
    /// parks the folder — five attempts, not six.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void AFolderIsParkedForAHumanOnTheFifthFailure(int attempts, bool expected) =>
        AudiobookMatchPolicy.IsPermanentFailure(attempts).Should().Be(expected);

    // ── Retry cooldown ───────────────────────────────────────────────────

    /// <summary>
    /// The scan runs daily so new audiobooks appear quickly. Without a cooldown,
    /// every folder that never matches would re-query all three sources every
    /// single day, forever.
    /// </summary>
    [Fact]
    public void AnUnmatchedFolderIsLeftAloneWithinTheCooldown()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        AudiobookMatchPolicy.IsInRetryCooldown(now.AddDays(-3), now).Should().BeTrue();
    }

    [Fact]
    public void AnUnmatchedFolderIsRetriedOnceTheCooldownElapses()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        AudiobookMatchPolicy.IsInRetryCooldown(now.AddDays(-8), now).Should().BeFalse();
    }

    /// <summary>Exactly at the boundary the cooldown is served, not still running.</summary>
    [Fact]
    public void TheCooldownEndsExactlyWhenItSaysItDoes()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        AudiobookMatchPolicy.IsInRetryCooldown(now - AudiobookMatchPolicy.UnmatchedRetryCooldown, now)
            .Should().BeFalse();
    }

    /// <summary>
    /// A folder with no recorded attempt has no cooldown to serve. Reading a
    /// missing timestamp as "just tried" would strand a sidecar that predates the
    /// field — it would be skipped on every scan and never retried.
    /// </summary>
    [Fact]
    public void AFolderNeverAttemptedIsNotInCooldown() =>
        AudiobookMatchPolicy.IsInRetryCooldown(null, DateTime.UtcNow).Should().BeFalse();

    /// <summary>
    /// A clock skew or a sidecar copied from a machine running ahead must not
    /// park a folder for a week and a half.
    /// </summary>
    [Fact]
    public void AFutureTimestampDoesNotExtendTheCooldownBeyondItsLength()
    {
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        AudiobookMatchPolicy.IsInRetryCooldown(now.AddDays(1), now).Should().BeTrue();
        AudiobookMatchPolicy.IsInRetryCooldown(now.AddDays(1), now.AddDays(8)).Should().BeFalse();
    }

    // ── Whether the folder is worth looking at at all ────────────────────

    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private static ScanDisposition Classify(
        string? renameStatus = null, string? matchStatus = null, DateTime? lastAttempted = null) =>
        AudiobookMatchPolicy.Classify(renameStatus, matchStatus, lastAttempted, Now);

    /// <summary>A folder with no sidecar at all is simply new.</summary>
    [Fact]
    public void AFolderWithNoSidecarIsIdentified() =>
        Classify().Should().Be(ScanDisposition.Identify);

    [Theory]
    [InlineData(AudiobookSidecarStatus.Renamed)]
    [InlineData(AudiobookSidecarStatus.Quarantined)]
    public void AFolderThatWasActuallyMovedIsLeftAlone(string renameStatus) =>
        Classify(renameStatus: renameStatus).Should().Be(ScanDisposition.AlreadyProcessed);

    /// <summary>
    /// The one that would be catastrophic to get wrong. A dry run writes a
    /// proposal and moves nothing, so it must not count as done — the scan ships
    /// with <c>DryRun = true</c> by default, so a dry-run status that suppressed
    /// the real pass would mean the service never renames anything while
    /// reporting that every folder is handled.
    /// </summary>
    [Theory]
    [InlineData(AudiobookSidecarStatus.DryRunProposed)]
    [InlineData(AudiobookSidecarStatus.DryRunProposedQuarantine)]
    public void ADryRunProposalDoesNotSuppressTheRealPass(string renameStatus) =>
        Classify(renameStatus: renameStatus).Should().Be(ScanDisposition.Identify);

    /// <summary>
    /// A folder killed between writing its intent and completing the move is
    /// resumed, not skipped — that pending sidecar is what makes the move
    /// restartable without paying for the lookups again.
    /// </summary>
    [Fact]
    public void AnInterruptedMoveIsResumed() =>
        Classify(renameStatus: AudiobookSidecarStatus.Pending, matchStatus: AudiobookSidecarStatus.Found)
            .Should().Be(ScanDisposition.Identify);

    [Fact]
    public void AFolderOutOfRetriesIsParkedForAPerson() =>
        Classify(matchStatus: AudiobookSidecarStatus.Failed).Should().Be(ScanDisposition.NeedsHuman);

    [Fact]
    public void ARecentlyUnmatchedFolderWaitsForItsRetryWindow() =>
        Classify(matchStatus: AudiobookSidecarStatus.Unmatched, lastAttempted: Now.AddDays(-2))
            .Should().Be(ScanDisposition.InRetryCooldown);

    [Fact]
    public void AnUnmatchedFolderPastItsWindowIsTriedAgain() =>
        Classify(matchStatus: AudiobookSidecarStatus.Unmatched, lastAttempted: Now.AddDays(-8))
            .Should().Be(ScanDisposition.Identify);

    /// <summary>
    /// Only the unmatched state observes the cooldown. A stale timestamp left on
    /// a folder in any other state must not stop it being looked at.
    /// </summary>
    [Fact]
    public void TheCooldownAppliesOnlyToUnmatchedFolders() =>
        Classify(matchStatus: AudiobookSidecarStatus.Found, lastAttempted: Now.AddHours(-1))
            .Should().Be(ScanDisposition.Identify);

    /// <summary>
    /// Order matters: a folder that failed repeatedly and was later matched and
    /// renamed is done, not a permanent failure. Checking "gave up" first would
    /// report it as failed on every scan forever.
    /// </summary>
    [Fact]
    public void HavingBeenRenamedOutranksHavingPreviouslyGivenUp() =>
        Classify(renameStatus: AudiobookSidecarStatus.Renamed, matchStatus: AudiobookSidecarStatus.Failed)
            .Should().Be(ScanDisposition.AlreadyProcessed);

    /// <summary>And giving up outranks the cooldown, so a parked folder stays
    /// parked rather than re-entering the retry rotation after a week.</summary>
    [Fact]
    public void GivingUpOutranksTheCooldown() =>
        Classify(matchStatus: AudiobookSidecarStatus.Failed, lastAttempted: Now.AddDays(-99))
            .Should().Be(ScanDisposition.NeedsHuman);

    /// <summary>
    /// These strings are written into ~1000 sidecar files on disk and read back
    /// case-sensitively by the status endpoint. Changing a value silently
    /// reclassifies every folder already processed, so the values are pinned here
    /// rather than only referenced by name.
    /// </summary>
    [Fact]
    public void ThePersistedStatusValuesAreFixed()
    {
        AudiobookSidecarStatus.Found.Should().Be("found");
        AudiobookSidecarStatus.Unmatched.Should().Be("unmatched");
        AudiobookSidecarStatus.Failed.Should().Be("failed");
        AudiobookSidecarStatus.Renamed.Should().Be("renamed");
        AudiobookSidecarStatus.Quarantined.Should().Be("quarantined");
        AudiobookSidecarStatus.Pending.Should().Be("pending");
        AudiobookSidecarStatus.SkippedNoMatch.Should().Be("skippedNoMatch");
        AudiobookSidecarStatus.NeedsHumanReview.Should().Be("needsHumanReview");
        AudiobookSidecarStatus.DryRunProposed.Should().Be("dryRunProposed");
        AudiobookSidecarStatus.DryRunProposedQuarantine.Should().Be("dryRunProposedQuarantine");
    }
}
