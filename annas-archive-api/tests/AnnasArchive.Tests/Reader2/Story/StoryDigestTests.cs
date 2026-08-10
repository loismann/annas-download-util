using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// What the extraction call is told about the cast so far.
///
/// <para>The digest is the one number standing between a 580-character novel and
/// a prompt that costs more than the summary it is extracting from. It is also
/// how the model resolves an alias — so what gets dropped decides whether the
/// next chapter recognises somebody or invents a duplicate of them.</para>
/// </summary>
public class StoryDigestTests
{
    private static Actor At(string id, ActorTier tier, int lastSeen) =>
        Actor(id, $"Actor {id}", tier, lastSeen: lastSeen);

    [Fact]
    public void The_digest_carries_the_four_cheap_fields_and_nothing_else()
    {
        var model = Model([
            Actor("a1", "Pyotr Bezukhov", ActorTier.Major, aliases: ["Pierre"]) with
            {
                Dossier = "an illegitimate son who inherits a fortune",
                Role = "the heir",
                Arc = [new ArcPoint(2, "duels with Dolokhov")]
            }
        ]);

        var digest = Cast.Digest(model, chapter: 5, maxActors: 50);

        digest.Should().Contain("a1").And.Contain("Pyotr Bezukhov").And.Contain("Pierre").And.Contain("major");

        // The three expensive fields. Each is many times the size of the four kept
        // ones, and none of them helps the model recognise a name.
        digest.Should().NotContain("illegitimate").And.NotContain("the heir").And.NotContain("duels");
    }

    [Fact]
    public void Groups_and_threads_are_an_id_and_a_name()
    {
        var model = Model(
            threads: [StoryThread("t1", "The Moscow salons", beats: [new Beat(1, "the soirée")])],
            groups: [new Group("g1", "The Rostovs", GroupKind.Family, ["a1"], [])]);

        var digest = Cast.Digest(model, chapter: 3, maxActors: 50);

        digest.Should().Contain("t1: The Moscow salons").And.Contain("g1: The Rostovs");
        digest.Should().NotContain("soirée");
    }

    /// <summary>The to-do's exact requirement, and the reason the ordering is tier-first.</summary>
    [Fact]
    public void Elision_drops_mentioned_before_minor_and_never_drops_a_major()
    {
        var actors = new[]
        {
            At("a1", ActorTier.Major, lastSeen: 1),
            At("a2", ActorTier.Minor, lastSeen: 2),
            At("a3", ActorTier.Mentioned, lastSeen: 2),
            At("a4", ActorTier.Secondary, lastSeen: 2)
        };

        var kept = Cast.Kept(actors, chapter: 3, maxActors: 3).Select(a => a.Id);

        kept.Should().Equal("a1", "a4", "a2");
    }

    /// <summary>
    /// The cap protects a budget. Dropping a protagonist breaks the feature, so
    /// majors go over the cap rather than out of it.
    /// </summary>
    [Fact]
    public void Every_major_survives_a_cap_smaller_than_the_principal_cast()
    {
        var actors = Enumerable.Range(1, 8).Select(i => At($"a{i}", ActorTier.Major, lastSeen: i)).ToArray();

        Cast.Kept(actors, chapter: 9, maxActors: 3).Should().HaveCount(8);
    }

    /// <summary>
    /// The recency half. A walk-on met two chapters ago is likelier to be named
    /// again — under a new name — than a secondary character last seen two hundred
    /// pages back, and it is the second who is safe to drop.
    /// </summary>
    [Fact]
    public void Somebody_long_off_the_page_is_dropped_before_somebody_just_met()
    {
        var actors = new[]
        {
            At("stale", ActorTier.Secondary, lastSeen: 1),
            At("fresh", ActorTier.Mentioned, lastSeen: 59)
        };

        Cast.Kept(actors, chapter: 60, maxActors: 1).Select(a => a.Id).Should().Equal("fresh");
    }

    [Fact]
    public void Under_the_cap_nobody_is_dropped()
    {
        var actors = new[] { At("a1", ActorTier.Mentioned, lastSeen: 0), At("a2", ActorTier.Minor, lastSeen: 0) };

        Cast.Kept(actors, chapter: 40, maxActors: 10).Should().HaveCount(2);
    }

    /// <summary>
    /// A digest that reordered itself would change the prompt input on every call
    /// for no reason — a wasted cache and an unreadable diff.
    /// </summary>
    [Fact]
    public void The_same_model_always_produces_the_same_digest()
    {
        var model = Model([At("a2", ActorTier.Minor, 3), At("a1", ActorTier.Minor, 3)]);
        var shuffled = model with { Actors = [.. model.Actors.Reverse()] };

        Cast.Digest(shuffled, 5, 50).Should().Be(Cast.Digest(model, 5, 50));
    }

    [Fact]
    public void An_empty_model_says_so_rather_than_sending_nothing()
    {
        Cast.Digest(StoryModel.Empty, chapter: 0, maxActors: 50).Should().Contain("nothing recorded");
    }
}
