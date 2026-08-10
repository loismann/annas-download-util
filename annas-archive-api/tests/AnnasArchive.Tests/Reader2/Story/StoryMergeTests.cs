using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// The merge rules, one per behaviour.
///
/// <para>These are the tests that matter most in Reader II. The merger is pure,
/// so every rule is checkable here without a model, a database, or a host — and
/// each of them is a way the accumulated model could go quietly wrong over three
/// hundred chapters, which is the only kind of wrong nobody reports.</para>
/// </summary>
public class StoryMergeTests
{
    // ─── nothing is ever lost ───────────────────────────────────────────

    [Fact]
    public void An_actor_is_never_deleted_by_extraction()
    {
        var model = Model([Actor("a1", "Dolokhov"), Actor("a2", "Denisov")]);

        // Nothing in the delta shape can remove anybody, and forty chapters that
        // never mention them must not either.
        var after = Merge(model, Delta(40, newActors: [Arriving("Petya Rostov")]));

        after.Actors.Select(a => a.CanonicalName)
            .Should().Contain(["Dolokhov", "Denisov", "Petya Rostov"]);
    }

    [Fact]
    public void An_actor_already_here_under_another_name_is_updated_rather_than_added()
    {
        var model = Model([Actor("a1", "Pyotr Kirillovich Bezukhov", ActorTier.Major)]);

        var after = Merge(model, Delta(4, newActors: [Arriving("Count Pyotr Bezukhov", arcChange: "inherits")]));

        after.Actors.Should().ContainSingle("a second entry for one person is the failure this prevents");
        after.ById("a1").Arc.Should().ContainSingle(p => p.Change == "inherits");
    }

    // ─── alias hints: propose, never merge ──────────────────────────────

    [Fact]
    public void A_confident_unambiguous_hint_is_applied()
    {
        var model = Model([Actor("a1", "Pyotr Bezukhov")]);

        var after = Merge(model, Delta(3, hints: [new AliasHint("Pierre", "a1", AliasConfidence.High)]));

        after.ById("a1").Aliases.Should().Contain("Pierre");
        after.CandidateMerges.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AliasConfidence.Medium)]
    [InlineData(AliasConfidence.Low)]
    public void A_hint_the_model_was_unsure_of_becomes_a_question(AliasConfidence confidence)
    {
        var model = Model([Actor("a1", "Pyotr Bezukhov")]);

        var after = Merge(model, Delta(3, hints: [new AliasHint("Pierre", "a1", confidence)]));

        after.ById("a1").Aliases.Should().BeEmpty("an uncertain hint must change nothing");
        after.CandidateMerges.Should().ContainSingle().Which.Alias.Should().Be("Pierre");
    }

    /// <summary>
    /// The case the whole design leans against. Two actors could both answer to
    /// the name, so applying the hint would fuse them — and a wrong fusion is
    /// invisible to the reader in a way a duplicate is not.
    /// </summary>
    [Fact]
    public void A_hint_naming_somebody_who_already_exists_is_never_auto_merged()
    {
        var model = Model([
            Actor("a1", "Nikolai Rostov"),
            Actor("a2", "Nikolai Bolkonsky", arc: [new ArcPoint(2, "refuses the match")])
        ]);

        var after = Merge(model, Delta(9, hints: [new AliasHint("Nikolai Bolkonsky", "a1", AliasConfidence.High)]));

        after.ById("a1").Aliases.Should().BeEmpty();
        after.Actors.Should().HaveCount(2);

        var question = after.CandidateMerges.Should().ContainSingle().Subject;
        question.ActorId.Should().Be("a1");
        question.OtherActorId.Should().Be("a2");
        question.Reason.Should().Contain("their own story", "an arc conflict is worth saying out loud");
    }

    [Fact]
    public void The_same_ambiguity_is_not_raised_twice()
    {
        var model = Model([Actor("a1", "Nikolai Rostov"), Actor("a2", "Nikolai Bolkonsky")]);
        var hint = new AliasHint("Nikolai Bolkonsky", "a1", AliasConfidence.High);

        var after = Merge(Merge(model, Delta(9, hints: [hint])), Delta(10, hints: [hint]));

        after.CandidateMerges.Should().ContainSingle(
            "a novel that keeps using a contested name would otherwise ask every chapter");
    }

    [Fact]
    public void A_hint_pointing_at_nobody_is_dropped_rather_than_inventing_an_actor()
    {
        var after = Merge(
            Model([Actor("a1", "Pierre")]),
            Delta(3, hints: [new AliasHint("Petya", "a99", AliasConfidence.High)]));

        after.Actors.Should().ContainSingle();
        after.CandidateMerges.Should().BeEmpty();
    }

    // ─── history is append-only ─────────────────────────────────────────

    [Fact]
    public void Arc_points_are_appended_chapter_tagged_and_deduplicated()
    {
        var model = Model([Actor("a1", "Pierre", arc: [new ArcPoint(1, "duels")])]);

        var after = Merge(model, Delta(5, updates: [new ActorUpdate("a1", ArcChange: "is taken prisoner")]));
        var again = Merge(after, Delta(6, updates: [new ActorUpdate("a1", ArcChange: "is taken prisoner")]));
        var same = Merge(again, Delta(7, updates: [new ActorUpdate("a1", ArcChange: "is taken prisoner")]));

        // Chapter 6 and 7 say the same thing, but they are different chapters, so
        // both are kept. The deduplication is on the pair, not on the text.
        same.ById("a1").Arc.Should().Equal(
            new ArcPoint(1, "duels"),
            new ArcPoint(5, "is taken prisoner"),
            new ArcPoint(6, "is taken prisoner"),
            new ArcPoint(7, "is taken prisoner"));
    }

    [Fact]
    public void The_same_arc_point_twice_in_one_chapter_is_recorded_once()
    {
        var after = Merge(
            Model([Actor("a1", "Pierre")]),
            Delta(5, updates: [
                new ActorUpdate("a1", ArcChange: "is taken prisoner"),
                new ActorUpdate("a1", ArcChange: "is taken prisoner")
            ]));

        after.ById("a1").Arc.Should().ContainSingle();
    }

    [Fact]
    public void Beats_are_appended_chapter_tagged_and_deduplicated()
    {
        var model = Model(threads: [StoryThread("t1", "The French advance", beats: [new Beat(1, "crosses the Niemen")])]);

        var after = Merge(model, Delta(2, beats: [
            new ThreadBeat("t1", "reaches Vilna"),
            new ThreadBeat("t1", "reaches Vilna")
        ]));

        after.Threads.Single().Beats.Should().Equal(
            new Beat(1, "crosses the Niemen"), new Beat(2, "reaches Vilna"));
    }

    /// <summary>An empty change is not history and must not become a blank entry.</summary>
    [Fact]
    public void Nothing_is_appended_for_a_chapter_with_nothing_to_say()
    {
        var after = Merge(
            Model([Actor("a1", "Pierre", arc: [new ArcPoint(1, "duels")])]),
            Delta(5, updates: [new ActorUpdate("a1", Role: "a count")]));

        after.ById("a1").Arc.Should().ContainSingle();
        after.ById("a1").Role.Should().Be("a count");
    }

    /// <summary>A chapter that says nothing about a role must not erase the role.</summary>
    [Fact]
    public void An_empty_field_never_overwrites_one_that_has_something_in_it()
    {
        var model = Model([Actor("a1", "Pierre") with { Role = "a count", Dossier = "an illegitimate son" }]);

        var after = Merge(model, Delta(5, updates: [new ActorUpdate("a1", ArcChange: "marries")]));

        after.ById("a1").Role.Should().Be("a count");
        after.ById("a1").Dossier.Should().Be("an illegitimate son");
    }

    // ─── tiers ──────────────────────────────────────────────────────────

    [Fact]
    public void A_tier_promotes_the_moment_the_model_says_so()
    {
        var after = Merge(
            Model([Actor("a1", "Petya", ActorTier.Mentioned)]),
            Delta(3, updates: [new ActorUpdate("a1", Tier: ActorTier.Major)]));

        after.ById("a1").Tier.Should().Be(ActorTier.Major);
    }

    /// <summary>
    /// A protagonist has quiet chapters, and a model reading one chapter at a time
    /// calls them minor in every one of them.
    /// </summary>
    [Fact]
    public void A_tier_does_not_demote_merely_because_a_chapter_was_quiet()
    {
        var model = Model([Actor("a1", "Pierre", ActorTier.Major, lastSeen: 8)]);

        var after = Merge(model, Delta(9, updates: [new ActorUpdate("a1", Tier: ActorTier.Minor)]));

        after.ById("a1").Tier.Should().Be(ActorTier.Major);
    }

    [Fact]
    public void A_tier_demotes_once_the_absence_is_long_enough()
    {
        var model = Model([Actor("a1", "Anna Pavlovna", ActorTier.Secondary, lastSeen: 5)]);

        var after = Merge(
            model,
            Delta(15, updates: [new ActorUpdate("a1", Tier: ActorTier.Minor)]),
            new StoryMergeRules(ThreadDormantAfterChapters: 10, TierDemotionAfterChapters: 10));

        after.ById("a1").Tier.Should().Be(ActorTier.Minor);
    }

    // ─── edges ──────────────────────────────────────────────────────────

    [Fact]
    public void Edges_key_on_the_pair_and_the_kind()
    {
        var model = Model([Actor("a1", "Pierre"), Actor("a2", "Helene")]);

        var after = Merge(model, Delta(3, edges: [
            new EdgeChange("a1", "a2", "married"),
            new EdgeChange("a1", "a2", "rival"),
            new EdgeChange("a1", "a2", "married", Note: "again")
        ]));

        after.Edges.Should().HaveCount(2, "one pair can be related in more than one way, but not twice the same way");
        after.Edges.Single(e => e.Type == "married").Note.Should().Be("again");
    }

    [Fact]
    public void Ending_a_relationship_keeps_it_and_records_when()
    {
        var model = Model([Actor("a1", "Pierre"), Actor("a2", "Helene")]);
        var married = Merge(model, Delta(3, edges: [new EdgeChange("a1", "a2", "married")]));

        var after = Merge(married, Delta(40, edges: [new EdgeChange("a1", "a2", "married", Ended: true)]));

        var edge = after.Edges.Should().ContainSingle().Subject;
        edge.SinceChapter.Should().Be(3);
        edge.EndedChapter.Should().Be(40, "when it ended is the interesting part; deleting it would assert it never was");
    }

    [Fact]
    public void An_edge_hanging_off_an_actor_that_does_not_exist_is_dropped()
    {
        var after = Merge(
            Model([Actor("a1", "Pierre")]),
            Delta(3, edges: [new EdgeChange("a1", "a99", "married"), new EdgeChange("a1", "a1", "rival")]));

        after.Edges.Should().BeEmpty("a relationship with nobody at one end is not a relationship");
    }

    // ─── threads and dormancy ───────────────────────────────────────────

    [Theory]
    [InlineData(9, ThreadStatus.Active)]
    [InlineData(10, ThreadStatus.Dormant)]
    [InlineData(11, ThreadStatus.Dormant)]
    public void A_thread_goes_dormant_exactly_at_the_threshold(int gap, ThreadStatus expected)
    {
        var model = Model(threads: [StoryThread("t1", "Natasha and Andrei", lastAdvanced: 20)]);

        var after = Merge(
            model, Delta(20 + gap, newActors: [Arriving("Someone Else")]),
            new StoryMergeRules(ThreadDormantAfterChapters: 10, TierDemotionAfterChapters: 10));

        after.Threads.Single().Status.Should().Be(expected);
    }

    [Fact]
    public void A_thread_advanced_by_this_very_chapter_is_never_swept()
    {
        var model = Model(threads: [StoryThread("t1", "The retreat", lastAdvanced: 5)]);

        var after = Merge(model, Delta(30, beats: [new ThreadBeat("t1", "Moscow burns")]));

        after.Threads.Single().Status.Should().Be(ThreadStatus.Active);
    }

    /// <summary>
    /// The mechanism behind "we have not seen this since chapter 61". A returning
    /// thread that came back silently would leave the reader with a name and no
    /// idea how long it has been.
    /// </summary>
    [Fact]
    public void A_dormant_thread_returning_records_how_long_it_was_gone()
    {
        var model = Model(threads: [StoryThread("t1", "Dolokhov's revenge", lastAdvanced: 61, status: ThreadStatus.Dormant)]);

        var after = Merge(model, Delta(74, beats: [new ThreadBeat("t1", "he reappears at the ford")]));

        var thread = after.Threads.Single();
        thread.Status.Should().Be(ThreadStatus.Active);
        thread.ReturnedInChapter.Should().Be(74);
        thread.ReturnedAfterChapters.Should().Be(13);
    }

    [Fact]
    public void A_thread_already_running_is_not_opened_a_second_time()
    {
        var model = Model(threads: [StoryThread("t1", "The French advance", lastAdvanced: 1)]);

        var after = Merge(model, Delta(4, newThreads: [
            new NewThread("the french advance", [], "reaches Smolensk")]));

        after.Threads.Should().ContainSingle();
        after.Threads.Single().Beats.Should().ContainSingle(b => b.WhatMoved == "reaches Smolensk");
    }

    // ─── idempotency ────────────────────────────────────────────────────

    /// <summary>
    /// The guarantee that makes a back-fill resumable. A merge is not reversible,
    /// so a second application would append every arc point and beat twice.
    /// </summary>
    [Fact]
    public void Re_ingesting_a_chapter_changes_nothing()
    {
        var delta = Delta(7,
            newActors: [Arriving("Platon Karataev", arcChange: "meets Pierre in captivity")],
            newThreads: [new NewThread("Captivity", [], "the column marches out")]);

        var once = Merge(Model(), delta);
        var twice = Merge(once, delta);

        twice.Should().BeEquivalentTo(once);
        twice.ChaptersIngested.Should().Equal(7);
    }

    [Fact]
    public void Every_ingested_chapter_is_recorded_in_order()
    {
        var after = Merge(Merge(Merge(Model(), Delta(3)), Delta(1)), Delta(2));

        after.ChaptersIngested.Should().Equal(1, 2, 3);
    }

    // ─── the reader's answer outranks the model's ───────────────────────

    /// <summary>
    /// The strongest signal in the model is the only one that came from a person.
    /// A hint the reader has refused must not be applied because a later chapter
    /// reported it one tier more confidently — that is the answer being overwritten
    /// by the thing it was given to correct.
    /// </summary>
    [Fact]
    public void A_refused_name_is_not_applied_however_confident_a_later_chapter_is()
    {
        var model = Model(
            actors: [Actor("a1", "Prince Andrew")],
            candidates: [new CandidateMerge("m1", "a1", null, "The Bear", "unsure", 1, Declined: true)],
            ingested: [0, 1]);

        var after = Merge(model, Delta(2, hints: [new AliasHint("The Bear", "a1", AliasConfidence.High)]));

        after.ById("a1").Aliases.Should().BeEmpty();
    }

    /// <summary>A question still open is not an answer, and does not block anything.</summary>
    [Fact]
    public void An_unanswered_question_does_not_block_a_confident_hint()
    {
        var model = Model(
            actors: [Actor("a1", "Prince Andrew")],
            candidates: [new CandidateMerge("m1", "a1", null, "The Bear", "unsure", 1)],
            ingested: [0, 1]);

        var after = Merge(model, Delta(2, hints: [new AliasHint("The Bear", "a1", AliasConfidence.High)]));

        after.ById("a1").Aliases.Should().Equal("The Bear");
    }

    // ─── names the digest carries ───────────────────────────────────────

    /// <summary>
    /// Every alias travels in the digest on every extraction call, so a name listed
    /// twice is paid for on every chapter for the life of the book.
    /// </summary>
    [Fact]
    public void A_new_actor_does_not_list_its_own_name_among_its_aliases()
    {
        var after = Merge(Model(), Delta(0, newActors: [
            Arriving("Pierre", aliases: ["Pierre", "Bezukhov"])]));

        after.Actors.Single().Aliases.Should().Equal("Bezukhov");
    }

    [Fact]
    public void A_rival_group_the_model_invented_is_not_recorded()
    {
        var after = Merge(Model(), Delta(0, groups: [
            new NewGroup("The Rostovs", GroupKind.Family, [], ["g99"])]));

        after.Groups.Single().RivalGroupIds.Should()
            .BeEmpty("nothing here ever removes a reference, so an invented one is permanent");
    }

    [Fact]
    public void A_group_records_the_chapter_it_first_appeared_in()
    {
        var after = Merge(Model(), Delta(12, groups: [
            new NewGroup("The Freemasons", GroupKind.SocialCircle, [], [])]));

        after.Groups.Single().FirstSeenChapter.Should().Be(12);
    }
}
