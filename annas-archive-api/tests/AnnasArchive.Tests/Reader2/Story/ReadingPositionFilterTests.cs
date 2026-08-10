using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// What a reader part-way through a book is allowed to see.
///
/// <para>The model is cumulative and the reader is not. Every one of these is a
/// way the panel could hand somebody the end of the book — and unlike a wrong
/// merge, a spoiler cannot be undone once it has been read.</para>
/// </summary>
public class ReadingPositionFilterTests
{
    /// <summary>Chapters 0-40 of a novel, as the model would hold them at the end.</summary>
    private static StoryModel WholeBook() => Model(
        actors: [
            Actor("a1", "Pierre", ActorTier.Major, firstSeen: 0, lastSeen: 40,
                arc: [new ArcPoint(2, "duels"), new ArcPoint(30, "is taken prisoner")]),
            Actor("a2", "Platon", ActorTier.Minor, firstSeen: 30, lastSeen: 38)
        ],
        threads: [
            StoryThread("t1", "Pierre's captivity", started: 30, lastAdvanced: 38,
                beats: [new Beat(30, "the column marches"), new Beat(38, "he is freed")]),
            StoryThread("t2", "The Moscow salons", started: 0, lastAdvanced: 12,
                status: ThreadStatus.Resolved, beats: [new Beat(0, "the soirée")])
        ],
        edges: [new Edge("a1", "a2", "befriends", SinceChapter: 30, EndedChapter: 38, Note: "")],
        groups: [new Group("g1", "The Bezukhovs", GroupKind.Family, ["a1", "a2"], [])],
        candidates: [new CandidateMerge("m1", "a1", "a2", "Karataev", "unsure", ProposedInChapter: 33)],
        ingested: [0, 2, 30, 38, 40]);

    private static StoryModel AtChapter(int chapter) => WholeBook().Through(chapter);

    [Fact]
    public void Somebody_the_reader_has_not_met_is_not_listed_at_all()
    {
        AtChapter(10).Actors.Select(a => a.Id).Should().Equal("a1");
        AtChapter(30).Actors.Select(a => a.Id).Should().Equal("a1", "a2");
    }

    [Fact]
    public void An_arc_stops_where_the_reader_stopped()
    {
        AtChapter(10).ById("a1").Arc.Should().Equal(new ArcPoint(2, "duels"));
    }

    /// <summary>
    /// Serving the real <c>lastSeenChapter</c> would say "last seen in chapter 40"
    /// to a reader in chapter ten, which tells them the character survives.
    /// </summary>
    [Fact]
    public void Last_seen_never_reports_a_chapter_the_reader_has_not_reached()
    {
        AtChapter(10).ById("a1").LastSeenChapter.Should().Be(10);
    }

    /// <summary>
    /// The subtle one. Hiding the actor but keeping the edge would leave the panel
    /// showing a relationship with a name the reader has never seen.
    /// </summary>
    [Fact]
    public void Every_reference_to_a_hidden_actor_goes_with_them()
    {
        var early = AtChapter(10);

        early.Edges.Should().BeEmpty();
        early.Groups.Single().MemberIds.Should().Equal("a1");
        early.CandidateMerges.Should().BeEmpty("a question naming somebody unmet is itself the spoiler");
    }

    [Fact]
    public void A_thread_that_has_not_started_is_not_shown()
    {
        AtChapter(10).Threads.Select(t => t.Id).Should().Equal("t2");
    }

    [Fact]
    public void A_threads_beats_stop_where_the_reader_stopped()
    {
        var captivity = AtChapter(33).Threads.Single(t => t.Id == "t1");

        captivity.Beats.Should().Equal(new Beat(30, "the column marches"));
        captivity.LastAdvancedChapter.Should().Be(30);
    }

    /// <summary>
    /// A thread the book resolves in chapter twelve is still running for a reader
    /// in chapter five, and saying "resolved" tells them it ends.
    /// </summary>
    [Fact]
    public void A_thread_resolved_later_still_reads_as_running()
    {
        AtChapter(5).Threads.Single(t => t.Id == "t2").Status.Should().Be(ThreadStatus.Active);
        AtChapter(20).Threads.Single(t => t.Id == "t2").Status.Should().Be(ThreadStatus.Resolved);
    }

    /// <summary>
    /// An edge that ends later has not ended yet. Showing <c>endedChapter: 38</c>
    /// to a reader in chapter 33 says the friendship is going to break.
    /// </summary>
    [Fact]
    public void A_relationship_that_ends_later_is_still_running()
    {
        AtChapter(33).Edges.Single().EndedChapter.Should().BeNull();
        AtChapter(38).Edges.Single().EndedChapter.Should().Be(38);
    }

    [Fact]
    public void Chapters_ingested_reports_only_what_is_behind_the_reader()
    {
        AtChapter(30).ChaptersIngested.Should().Equal(0, 2, 30);
    }

    [Fact]
    public void A_reader_who_has_not_started_sees_only_the_opening_chapter()
    {
        var start = AtChapter(0);

        start.Actors.Should().ContainSingle(a => a.Id == "a1");
        start.Threads.Should().ContainSingle(t => t.Id == "t2");
    }

    /// <summary>Filtering twice is filtering once. The panel re-filters what it holds.</summary>
    [Fact]
    public void The_filter_is_idempotent()
    {
        AtChapter(20).Through(20).Should().BeEquivalentTo(AtChapter(20));
    }

    // ─── groups ─────────────────────────────────────────────────────────

    /// <summary>
    /// A faction name is often the plot. Hiding its members while serving the group
    /// itself would name the conspiracy and only withhold who is in it.
    /// </summary>
    [Fact]
    public void A_group_formed_later_is_not_named_to_a_reader_who_has_not_reached_it()
    {
        var model = Model(
            actors: [Actor("a9", "Bazdeev", firstSeen: 40, lastSeen: 40)],
            groups: [new Group("g1", "The Freemasons", GroupKind.SocialCircle, ["a9"], [], 40)]);

        model.Through(3).Groups.Should().BeEmpty();
        model.Through(40).Groups.Should().ContainSingle();
    }

    // ─── thread status is recomputed, never read from storage ───────────

    /// <summary>
    /// The stored status is the one the thread reached <i>latest</i>. Serving it
    /// unchanged tells a reader in chapter thirty that a thread they have not heard
    /// of in fifteen chapters is still running — which is to say, that it comes
    /// back.
    /// </summary>
    [Fact]
    public void A_thread_revived_later_still_reads_as_dormant_where_it_was_quiet()
    {
        var model = Model(threads: [
            StoryThread("t1", "Dolokhov's debt", started: 5, lastAdvanced: 45,
                beats: [new Beat(5, "the card game"), new Beat(45, "he reappears")])]);

        model.Through(30).Threads.Single().Status.Should().Be(ThreadStatus.Dormant);
        model.Through(45).Threads.Single().Status.Should().Be(ThreadStatus.Active);
    }

    /// <summary>A thread advanced recently is running, whatever storage says.</summary>
    [Fact]
    public void A_thread_that_moved_within_the_window_reads_as_active()
    {
        var model = Model(threads: [
            StoryThread("t1", "The siege", started: 5, lastAdvanced: 40, status: ThreadStatus.Dormant,
                beats: [new Beat(5, "the guns open"), new Beat(28, "the wall falls")])]);

        model.Through(30).Threads.Single().Status.Should().Be(ThreadStatus.Active);
    }

    /// <summary>Resolved in the past stays resolved; only a later ending is undone.</summary>
    [Fact]
    public void A_thread_already_finished_is_not_reopened_by_the_filter()
    {
        var model = Model(threads: [
            StoryThread("t1", "The Moscow salons", started: 0, lastAdvanced: 12,
                status: ThreadStatus.Resolved, beats: [new Beat(0, "the soirée"), new Beat(12, "the funeral")])]);

        model.Through(30).Threads.Single().Status.Should().Be(ThreadStatus.Resolved);
    }
}
