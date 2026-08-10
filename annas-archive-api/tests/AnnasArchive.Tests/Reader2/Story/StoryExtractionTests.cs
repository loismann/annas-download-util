using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Reading what the model answered.
///
/// <para>Lenient about shape, strict about belief. Losing a whole chapter's
/// extraction to a stray code fence would be absurd; believing an alias because a
/// confidence field was unreadable would be dangerous. Every fallback here goes
/// the cautious way.</para>
/// </summary>
public class StoryExtractionTests
{
    private static StoryDelta Parse(string json) => StoryExtraction.Parse(json, chapter: 7);

    [Fact]
    public void A_full_delta_is_read_into_its_parts()
    {
        var delta = Parse("""
            {
              "newActors": [{"canonicalName": "Platon Karataev", "aliases": ["Platon"], "tier": "minor",
                             "role": "a peasant soldier", "arcChange": "meets Pierre"}],
              "actorUpdates": [{"actorId": "a1", "tier": "major", "arcChange": "is taken prisoner"}],
              "aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": "high"}],
              "newGroups": [{"name": "The prisoners", "kind": "social-circle", "memberIds": ["a1"]}],
              "groupUpdates": [{"groupId": "g1", "memberIds": ["a2"]}],
              "edgeChanges": [{"from": "a1", "to": "a2", "type": "befriends", "note": "in captivity"}],
              "newThreads": [{"name": "Captivity", "participantIds": ["a1"], "firstBeat": "the column marches"}],
              "threadBeats": [{"threadId": "t1", "whatMoved": "Moscow burns"}]
            }
            """);

        delta.Chapter.Should().Be(7);
        delta.NewActors.Single().CanonicalName.Should().Be("Platon Karataev");
        delta.NewActors.Single().Tier.Should().Be(ActorTier.Minor);
        delta.ActorUpdates.Single().Tier.Should().Be(ActorTier.Major);
        delta.AliasHints.Single().Confidence.Should().Be(AliasConfidence.High);
        delta.NewGroups.Single().Kind.Should().Be(GroupKind.SocialCircle);
        delta.GroupUpdates.Single().MemberIds.Should().Equal("a2");
        delta.EdgeChanges.Single().Note.Should().Be("in captivity");
        delta.NewThreads.Single().FirstBeat.Should().Be("the column marches");
        delta.ThreadBeats.Single().WhatMoved.Should().Be("Moscow burns");
    }

    [Fact]
    public void A_missing_key_is_an_empty_list_rather_than_a_failure()
    {
        var delta = Parse("""{"newActors": []}""");

        delta.ActorUpdates.Should().BeEmpty();
        delta.ThreadBeats.Should().BeEmpty();
    }

    /// <summary>Models fence JSON however firmly they are asked not to.</summary>
    [Theory]
    [InlineData("```json\n{\"newActors\": [{\"canonicalName\": \"Pierre\"}]}\n```")]
    [InlineData("```\n{\"newActors\": [{\"canonicalName\": \"Pierre\"}]}\n```")]
    public void A_code_fence_around_the_answer_is_stripped(string answer)
    {
        Parse(answer).NewActors.Single().CanonicalName.Should().Be("Pierre");
    }

    /// <summary>Thirty good actors are worth more than a clean error about the thirty-first.</summary>
    [Fact]
    public void One_unreadable_entry_does_not_lose_the_rest()
    {
        var delta = Parse("""
            {"newActors": [{"canonicalName": "Pierre"}, "not an object", {"canonicalName": "Natasha"}]}
            """);

        delta.NewActors.Select(a => a.CanonicalName).Should().Equal("Pierre", "Natasha");
    }

    // ─── the cautious fallbacks ─────────────────────────────────────────

    /// <summary>
    /// The one value that decides whether two names are merged without anybody
    /// looking. Anything unreadable has to land on the side that asks.
    /// </summary>
    [Theory]
    [InlineData("""{"aliasHints": [{"alias": "Pierre", "actorId": "a1"}]}""")]
    [InlineData("""{"aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": "fairly sure"}]}""")]
    [InlineData("""{"aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": null}]}""")]
    public void An_unreadable_confidence_is_the_lowest_one(string json)
    {
        Parse(json).AliasHints.Single().Confidence.Should().Be(AliasConfidence.Low);
    }

    [Theory]
    [InlineData(0.95, AliasConfidence.High)]
    [InlineData(0.9, AliasConfidence.High)]
    [InlineData(0.85, AliasConfidence.Medium)]
    [InlineData(0.3, AliasConfidence.Low)]
    public void A_numeric_confidence_is_read_on_a_deliberately_high_bar(double given, AliasConfidence expected)
    {
        Parse($$"""{"aliasHints": [{"alias": "Pierre", "actorId": "a1", "confidence": {{given}}}]}""")
            .AliasHints.Single().Confidence.Should().Be(expected);
    }

    /// <summary>An unreadable tier claims the least, so it can only be promoted from.</summary>
    [Fact]
    public void An_unreadable_tier_is_the_lowest_one()
    {
        Parse("""{"newActors": [{"canonicalName": "Pierre", "tier": "quite important"}]}""")
            .NewActors.Single().Tier.Should().Be(ActorTier.Mentioned);
    }

    [Fact]
    public void An_unreadable_group_kind_is_other()
    {
        Parse("""{"newGroups": [{"name": "The salon", "kind": "a gathering"}]}""")
            .NewGroups.Single().Kind.Should().Be(GroupKind.Other);
    }

    /// <summary>
    /// A null field means "unchanged", not "empty". Reading it as an empty string
    /// would let a chapter with nothing to say about a role erase the role.
    /// </summary>
    [Fact]
    public void An_absent_update_field_stays_absent()
    {
        var update = Parse("""{"actorUpdates": [{"actorId": "a1", "arcChange": "marries"}]}""")
            .ActorUpdates.Single();

        update.Role.Should().BeNull();
        update.Tier.Should().BeNull();
        update.GroupIds.Should().BeNull();
    }

    [Fact]
    public void An_answer_that_is_not_json_is_reported_rather_than_guessed_at()
    {
        StoryExtraction.TryParse("I could not find any characters.", 7, out var delta).Should().BeFalse();
        delta.Should().BeEquivalentTo(StoryDelta.Empty(7));
    }

    [Fact]
    public void An_answer_that_is_json_but_not_an_object_is_refused()
    {
        StoryExtraction.TryParse("""["Pierre", "Natasha"]""", 7, out _).Should().BeFalse();
    }
}
