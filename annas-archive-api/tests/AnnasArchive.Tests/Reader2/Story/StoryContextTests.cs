using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// The story-so-far block a chapter summary is given (spec Phase 9).
/// </summary>
public class StoryContextTests
{
    private static readonly StoryVocabulary Fiction = new("Characters", "Factions", "Plot threads");

    private static string? Build(StoryModel model, int chapter = 10) =>
        StoryContext.Build(model, Fiction, chapter, maxActors: 120, recentChapters: 20);

    [Fact]
    public void An_empty_record_contributes_nothing_rather_than_an_empty_heading()
    {
        Build(Model()).Should().BeNull();
    }

    /// <summary>The fact "Who appears" exists for: how long somebody has been gone.</summary>
    [Fact]
    public void Every_reminder_says_when_the_person_was_last_seen()
    {
        var block = Build(Model(actors: [
            Actor("a1", "Dolokhov", lastSeen: 4) with { Role = "a duellist" }]));

        block.Should().Contain("Dolokhov — a duellist; last seen in chapter 5");
        block.Should().Contain("Characters", "the labels are the lens's, not a hard-coded table");
    }

    [Fact]
    public void Open_threads_are_listed_and_quiet_ones_say_since_when()
    {
        var block = Build(Model(threads: [
            StoryThread("t1", "The debt", lastAdvanced: 9),
            StoryThread("t2", "The salons", lastAdvanced: 1, status: ThreadStatus.Dormant)]));

        block.Should().Contain("The debt — last moved in chapter 10");
        block.Should().Contain("The salons — nothing since chapter 2");
    }

    /// <summary>A finished thread is not "running in parallel", and saying so invites
    /// "meanwhile" sentences about things that are over.</summary>
    [Fact]
    public void A_resolved_thread_is_not_offered_as_running_elsewhere()
    {
        Build(Model(threads: [
            StoryThread("t1", "The duel", status: ThreadStatus.Resolved)]))
            .Should().BeNull();
    }

    /// <summary>The digest cap, reused: one rule for who is worth naming to a model.</summary>
    [Fact]
    public void The_cast_is_capped_by_the_same_rule_as_the_digest()
    {
        var crowd = Enumerable.Range(0, 30)
            .Select(i => Actor($"a{i}", $"Walk-on {i}", ActorTier.Mentioned, lastSeen: 9))
            .Append(Actor("a99", "Pierre", ActorTier.Major, lastSeen: 0))
            .ToArray();

        var block = StoryContext.Build(
            Model(actors: crowd), Fiction, chapter: 10, maxActors: 5, recentChapters: 20);

        block.Should().Contain("Pierre", "a major is never dropped");
        block!.Split('\n').Count(l => l.StartsWith("- ")).Should().Be(5);
    }

    [Fact]
    public void The_block_says_it_is_a_record_of_earlier_chapters()
    {
        Build(Model(actors: [Actor("a1", "Pierre")]))
            .Should().Contain("not part of this one");
    }
}
