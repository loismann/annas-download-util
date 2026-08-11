using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// What one part of a delta means when it points at another.
///
/// <para><b>The bug these are written against emptied the feature.</b> Ids are
/// assigned by the merge and reach the model only through the digest, so somebody
/// introduced in this chapter has no id the extraction could have used. While
/// references were matched on id alone, every relationship between two people who
/// arrived together was dropped — and on a book's first ingest that is every
/// relationship in it. Thirty-two characters were recorded from two chapters with
/// no edge between any of them, and the map drew thirty-two islands.</para>
///
/// <para>So a reference is an id <i>or</i> a name. What has not changed is that a
/// reference resolving to nothing is still dropped: this widened what can be
/// resolved, and took nothing off the guard.</para>
/// </summary>
public class StoryReferenceTests
{
    // ─── the headline case ──────────────────────────────────────────────

    [Fact]
    public void Two_people_who_arrive_in_one_chapter_can_be_related_to_each_other()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn"), Arriving("Ellie"), Arriving("Josias Aponi")],
            edges: [new EdgeChange("Finn", "Ellie", "travels-with"),
                    new EdgeChange("Josias Aponi", "Finn", "protects")]));

        after.Edges.Should().HaveCount(2,
            "nobody had an id before this chapter, so an id-only reference could name none of them");
    }

    [Fact]
    public void An_edge_named_by_name_is_stored_under_ids()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn"), Arriving("Ellie")],
            edges: [new EdgeChange("Finn", "Ellie", "travels-with")]));

        var edge = after.Edges.Single();

        // Stored as a name, the edge would be unfindable the moment a later
        // chapter reported the same pair by their digest ids, and the model would
        // grow a second copy of every relationship it already held.
        edge.From.Should().Be(after.Actors.Single(a => a.CanonicalName == "Finn").Id);
        edge.To.Should().Be(after.Actors.Single(a => a.CanonicalName == "Ellie").Id);
    }

    [Fact]
    public void An_id_and_a_name_for_one_person_reach_the_same_edge()
    {
        var model = Merge(Model(), Delta(1,
            newActors: [Arriving("Finn"), Arriving("Ellie")],
            edges: [new EdgeChange("Finn", "Ellie", "travels-with", Note: "on the run")]));

        var after = Merge(model, Delta(2, edges: [new EdgeChange("a1", "a2", "travels-with", Note: "still")]));

        after.Edges.Should().ContainSingle("this is the same relationship reported a second way");
        after.Edges.Single().Notes.Select(n => n.What).Should().Equal("on the run", "still");
    }

    // ─── the guard is still a guard ─────────────────────────────────────

    [Fact]
    public void An_edge_naming_somebody_who_is_not_here_is_still_dropped()
    {
        var after = Merge(Model([Actor("a1", "Finn")]),
            Delta(11, edges: [new EdgeChange("Finn", "Someone Never Mentioned", "rival")]));

        after.Edges.Should().BeEmpty();
    }

    /// <summary>
    /// Two actors answering to one name survive only when the reader has refused
    /// to merge them. Picking either would hang the relationship on a person the
    /// book never gave it to, which is the kind of wrong nobody can see.
    /// </summary>
    [Fact]
    public void A_name_belonging_to_two_people_resolves_to_neither()
    {
        var model = Model([Actor("a1", "Lord Valdier"), Actor("a2", "Valdier"), Actor("a3", "Heba")]);

        var after = Merge(model, Delta(11, edges: [new EdgeChange("Valdier", "Heba", "employs")]));

        after.Edges.Should().BeEmpty("an ambiguous name is a question, never a guess");
    }

    [Fact]
    public void Somebody_cannot_be_related_to_themselves_under_two_names()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu", aliases: ["Finn"])]);

        var after = Merge(model, Delta(11, edges: [new EdgeChange("Finn", "a1", "rival")]));

        after.Edges.Should().BeEmpty("both references are one person, and a node cannot flow to itself");
    }

    // ─── groups, from either direction ──────────────────────────────────

    [Fact]
    public void A_group_founded_and_populated_in_one_chapter_has_its_members()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn"), Arriving("Ellie")],
            groups: [new NewGroup("The Jalgori", GroupKind.Family, ["Finn", "Ellie"], [])]));

        after.Groups.Single().MemberIds.Should().HaveCount(2);
    }

    /// <summary>
    /// The other direction: the actor claims the group. Actors are admitted before
    /// groups are opened, so at the moment this reference is read the group does
    /// not exist yet — which is why membership is settled in a pass of its own.
    /// </summary>
    [Fact]
    public void An_actor_claiming_a_group_founded_in_the_same_chapter_is_put_in_it()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn", groups: ["The Jalgori"])],
            groups: [new NewGroup("The Jalgori", GroupKind.Family, [], [])]));

        after.Groups.Single().MemberIds.Should().Equal(after.Actors.Single().Id);
    }

    [Fact]
    public void Membership_is_written_on_both_the_actor_and_the_group()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn")],
            groups: [new NewGroup("The Jalgori", GroupKind.Family, ["Finn"], [])]));

        var group = after.Groups.Single();
        var actor = after.Actors.Single();

        // The cast list reads one side and the filters read the other. Two places
        // holding one fact must never be able to disagree about it.
        group.MemberIds.Should().Equal(actor.Id);
        actor.GroupIds.Should().Equal(group.Id);
    }

    [Fact]
    public void A_group_naming_a_member_who_is_not_here_gains_nobody()
    {
        var after = Merge(Model(), Delta(11,
            groups: [new NewGroup("The Jalgori", GroupKind.Family, ["Nobody At All"], [])]));

        after.Groups.Single().MemberIds.Should().BeEmpty();
    }

    [Fact]
    public void A_group_is_never_recorded_as_its_own_rival()
    {
        var after = Merge(Model(), Delta(11,
            groups: [new NewGroup("The Jalgori", GroupKind.Family, [], ["The Jalgori"])]));

        after.Groups.Single().RivalGroupIds.Should().BeEmpty("that would draw an edge from a node to itself");
    }

    [Fact]
    public void Two_groups_founded_together_can_be_rivals()
    {
        var after = Merge(Model(), Delta(11, groups: [
            new NewGroup("The Jalgori", GroupKind.Family, [], []),
            new NewGroup("The Heresy Dominion", GroupKind.PoliticalFaction, [], ["The Jalgori"])
        ]));

        var dominion = after.Groups.Single(g => g.Name == "The Heresy Dominion");

        dominion.RivalGroupIds.Should().Equal(after.Groups.Single(g => g.Name == "The Jalgori").Id);
    }

    // ─── threads ────────────────────────────────────────────────────────

    [Fact]
    public void A_thread_can_name_its_participants_as_they_arrive()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn"), Arriving("Ellie")],
            newThreads: [new NewThread("Finn's flight", ["Finn", "Ellie"], "they run")]));

        after.Threads.Single().ParticipantIds.Should().HaveCount(2);
    }

    [Fact]
    public void A_beat_can_name_the_thread_it_moves()
    {
        var model = Merge(Model(), Delta(11,
            newThreads: [new NewThread("Finn's flight", [], "they run")]));

        var after = Merge(model, Delta(12, beats: [new ThreadBeat("Finn's flight", "they are cornered")]));

        after.Threads.Single().Beats.Should().HaveCount(2);
    }

    // ─── how two people know each other, accumulated ────────────────────

    /// <summary>
    /// The chapter that made two people allies and the chapter that strained it
    /// are both the answer to "how do these two know each other". While this was
    /// one overwritten string, the record could describe a relationship it had no
    /// way to account for.
    /// </summary>
    [Fact]
    public void What_passes_between_two_people_is_kept_chapter_by_chapter()
    {
        var model = Model([Actor("a1", "Finn"), Actor("a2", "Liliana")]);

        var after = new[] { (5, "she vouches for him"), (9, "she treats him coldly") }
            .Aggregate(model, (current, said) =>
                Merge(current, Delta(said.Item1,
                    edges: [new EdgeChange("a1", "a2", "allied", Note: said.Item2)])));

        after.Edges.Single().Notes.Should().Equal(
            new EdgeNote(5, "she vouches for him"),
            new EdgeNote(9, "she treats him coldly"));
    }

    [Fact]
    public void A_reader_is_told_only_the_part_of_a_relationship_they_have_reached()
    {
        var model = Model([Actor("a1", "Finn"), Actor("a2", "Liliana")]);

        var after = new[] { (5, "she vouches for him"), (9, "she betrays him") }
            .Aggregate(model, (current, said) =>
                Merge(current, Delta(said.Item1,
                    edges: [new EdgeChange("a1", "a2", "allied", Note: said.Item2)])));

        after.Through(6).Edges.Single().Notes
            .Should().ContainSingle().Which.What.Should().Be("she vouches for him");
    }

    [Fact]
    public void One_chapter_saying_the_same_thing_twice_records_it_once()
    {
        var model = Model([Actor("a1", "Finn"), Actor("a2", "Liliana")]);

        var after = Merge(model, Delta(5, edges: [
            new EdgeChange("a1", "a2", "allied", Note: "she vouches for him"),
            new EdgeChange("a1", "a2", "allied", Note: "she vouches for him")
        ]));

        after.Edges.Single().Notes.Should().ContainSingle();
    }

    // ─── actor updates and hints ────────────────────────────────────────

    [Fact]
    public void An_update_can_name_somebody_introduced_in_the_same_chapter()
    {
        var after = Merge(Model(), Delta(11,
            newActors: [Arriving("Finn")],
            updates: [new ActorUpdate("Finn", Dossier: "the heir, on the run")]));

        after.Actors.Single().Dossier.Should().Be("the heir, on the run");
    }

    [Fact]
    public void An_alias_hint_can_name_its_target_by_name()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu")]);

        var after = Merge(model, Delta(11,
            hints: [new AliasHint("Finn", "Finbar Jalgori-Tobu", AliasConfidence.High)]));

        after.ById("a1").Aliases.Should().Contain("Finn");
    }
}
