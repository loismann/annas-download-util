using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Answering the merger's questions — the only path by which an actor is ever
/// removed.
///
/// <para>Extraction never deletes anybody, so everything here happens because
/// somebody looked at two entries and said they were one person. That makes it
/// the one place a fuse can go wrong without a model being involved.</para>
/// </summary>
public class MergeResolutionTests
{
    private static StoryModel TwoEntries(bool declined = false) => Model(
        actors: [
            Actor("a1", "Pierre Bezukhov", ActorTier.Major, firstSeen: 0, lastSeen: 20,
                aliases: ["Bezukhov"], arc: [new ArcPoint(2, "duels Dolokhov")]),
            Actor("a2", "Pyotr Kirillovich", ActorTier.Minor, firstSeen: 5, lastSeen: 18,
                aliases: ["Bezúkhov"], arc: [new ArcPoint(5, "inherits")])
        ],
        candidates: [new CandidateMerge("m1", "a1", "a2", "Pyotr Kirillovich", "unsure", 6, declined)]);

    private static StoryModel Answer(bool accept, StoryModel? model = null) =>
        MergeResolution.Resolve(model ?? TwoEntries(), "m1", accept);

    [Fact]
    public void Saying_yes_leaves_one_entry_holding_both_histories()
    {
        var after = Answer(accept: true);

        after.Actors.Should().ContainSingle();
        after.ById("a1").Arc.Select(p => p.Chapter).Should().Equal(2, 5);
        after.ById("a1").Tier.Should().Be(ActorTier.Major, "the higher of the two is the true one");
        after.ById("a1").FirstSeenChapter.Should().Be(0);
    }

    /// <summary>
    /// A fuse is where two spellings of one person meet, so it is the last place
    /// that may run a weaker rule than the merge does. Deduplicating by string
    /// would leave "Bezúkhov" sitting next to "Bezukhov" in the list the fuse
    /// existed to tidy.
    /// </summary>
    [Fact]
    public void Fusing_does_not_leave_two_spellings_of_one_name()
    {
        Answer(accept: true).ById("a1").Aliases.Should().NotContain("Bezúkhov");
    }

    [Fact]
    public void Saying_no_keeps_both_and_marks_the_question_answered()
    {
        var after = Answer(accept: false);

        after.Actors.Should().HaveCount(2);
        after.CandidateMerges.Single().Declined.Should().BeTrue();
    }

    /// <summary>A question already answered is not a second chance to change it.</summary>
    [Fact]
    public void An_answered_question_cannot_be_answered_again()
    {
        Answer(accept: true, TwoEntries(declined: true)).Actors.Should().HaveCount(2);
    }

    [Fact]
    public void Nothing_left_pointing_at_the_absorbed_entry()
    {
        var model = TwoEntries() with
        {
            Edges = [new Edge("a2", "a1", "married", 6, null, "")],
            Groups = [new Group("g1", "The Bezukhovs", GroupKind.Family, ["a1", "a2"], [])],
            Threads = [StoryThread("t1", "The inheritance", started: 5) with { ParticipantIds = ["a2"] }]
        };

        var after = MergeResolution.Resolve(model, "m1", accept: true);

        after.Edges.Should().BeEmpty("they were one person, so it was never a relationship");
        after.Groups.Single().MemberIds.Should().Equal("a1");
        after.Threads.Single().ParticipantIds.Should().Equal("a1");
    }

    /// <summary>
    /// The same pair seen twice is the same relationship. An ended edge beside a
    /// running one did not end.
    /// </summary>
    [Fact]
    public void A_relationship_still_running_under_either_entry_is_still_running()
    {
        var model = TwoEntries() with
        {
            Actors = [.. TwoEntries().Actors, Actor("a3", "Natasha")],
            Edges = [
                new Edge("a1", "a3", "loves", 10, EndedChapter: 15, ""),
                new Edge("a2", "a3", "loves", 12, EndedChapter: null, "")
            ]
        };

        var after = MergeResolution.Resolve(model, "m1", accept: true);

        after.Edges.Should().ContainSingle();
        after.Edges.Single().EndedChapter.Should().BeNull();
        after.Edges.Single().SinceChapter.Should().Be(10);
    }
}
