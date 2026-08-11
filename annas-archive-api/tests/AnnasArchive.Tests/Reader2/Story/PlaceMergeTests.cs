using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Places, folded in one chapter at a time.
///
/// <para>The same alias discipline as the cast, for the same reason: a book that
/// calls one city Ravensmarch, the Marches, and "the capital" would otherwise be
/// recorded as having three cities, and a reader asking where they last saw
/// somebody gets three answers.</para>
///
/// <para>Deliberately simpler in one respect. Places raise no questions for the
/// reader — a wrong place merge is cheaply visible and cheaply undone, while a
/// wrong <i>person</i> merge is a story nobody can see is wrong.</para>
/// </summary>
public class PlaceMergeTests
{
    private static StoryModel WithPlaces(params NewPlace[] arriving) =>
        Merge(StoryModel.Empty, Delta(0, newPlaces: arriving));

    [Fact]
    public void A_chapter_can_introduce_somewhere()
    {
        var model = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, description: "A river capital."));

        var place = model.Places.Should().ContainSingle().Subject;
        place.Name.Should().Be("Ravensmarch");
        place.Kind.Should().Be(PlaceKind.Settlement);
        place.Description.Should().Be("A river capital.");
        place.FirstSeenChapter.Should().Be(0);
    }

    [Fact]
    public void A_place_named_again_under_an_alias_is_the_same_place()
    {
        var first = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, ["the Marches"]));
        var again = Merge(first, Delta(1, newPlaces: [Arriving("the Marches", PlaceKind.Settlement)]));

        again.Places.Should().ContainSingle();
        again.Places[0].LastSeenChapter.Should().Be(1);
    }

    /// <summary>A place listing its own name among its aliases would be in the digest twice, forever.</summary>
    [Fact]
    public void A_places_own_name_is_never_kept_as_one_of_its_aliases()
    {
        var model = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, ["Ravensmarch", "the Marches"]));

        model.Places[0].Aliases.Should().Equal("the Marches");
    }

    /// <summary>
    /// A chapter that mentions somewhere in passing must not empty the description
    /// written by the chapter that arrived there.
    /// </summary>
    [Fact]
    public void A_passing_mention_does_not_erase_what_is_already_recorded()
    {
        var first = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, description: "A river capital."));
        var again = Merge(first, Delta(1, newPlaces: [Arriving("Ravensmarch", PlaceKind.Settlement)]));

        again.Places[0].Description.Should().Be("A river capital.");
    }

    [Fact]
    public void An_update_changes_what_it_names_and_leaves_the_rest()
    {
        var first = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, description: "A river capital."));

        var again = Merge(first, Delta(1, placeUpdates:
            [new PlaceUpdate(first.Places[0].Id, Description: "Burned in the siege.")]));

        again.Places[0].Description.Should().Be("Burned in the siege.");
        again.Places[0].Kind.Should().Be(PlaceKind.Settlement);
    }

    /// <summary>The model does not assign ids, so an id we do not know is a mistake.</summary>
    [Fact]
    public void An_update_naming_nowhere_is_dropped_rather_than_invented_into_a_place()
    {
        var model = Merge(StoryModel.Empty, Delta(0, placeUpdates: [new PlaceUpdate("p99", Description: "x")]));

        model.Places.Should().BeEmpty();
    }

    // ─── what contains what ─────────────────────────────────────────────

    /// <summary>
    /// The reason references are resolved after everything is admitted: a house
    /// named in the same chapter as its city has no id when the model writes it.
    /// </summary>
    [Fact]
    public void A_place_can_sit_inside_one_introduced_in_the_same_chapter()
    {
        var model = WithPlaces(
            Arriving("The Gate House", PlaceKind.Building, partOf: "Ravensmarch"),
            Arriving("Ravensmarch", PlaceKind.Settlement));

        var house = model.Places.Single(p => p.Name == "The Gate House");
        var city = model.Places.Single(p => p.Name == "Ravensmarch");

        house.PartOf.Should().Be(city.Id);
    }

    [Fact]
    public void A_container_that_names_nowhere_leaves_the_place_uncontained()
    {
        var model = WithPlaces(Arriving("The Gate House", PlaceKind.Building, partOf: "Atlantis"));

        model.Places[0].PartOf.Should().BeEmpty();
    }

    [Fact]
    public void A_place_is_never_put_inside_itself()
    {
        var model = WithPlaces(Arriving("Ravensmarch", PlaceKind.Settlement, partOf: "Ravensmarch"));

        model.Places[0].PartOf.Should().BeEmpty();
    }

    /// <summary>
    /// A cycle is not a tidiness problem: the panel walks the chain upward to say
    /// where somewhere is, and a loop would walk forever.
    /// </summary>
    [Fact]
    public void A_place_is_never_put_inside_something_it_already_contains()
    {
        var first = WithPlaces(
            Arriving("Ravensmarch", PlaceKind.Settlement),
            Arriving("The Gate House", PlaceKind.Building, partOf: "Ravensmarch"));

        var again = Merge(first, Delta(1, placeUpdates:
            [new PlaceUpdate("Ravensmarch", PartOf: "The Gate House")]));

        again.Places.Single(p => p.Name == "Ravensmarch").PartOf.Should().BeEmpty();
    }

    // ─── the reading-position filter ────────────────────────────────────

    [Fact]
    public void A_place_first_seen_ahead_of_the_reader_is_not_served()
    {
        var model = Model(places: [Place("p1", "Ravensmarch", firstSeen: 0), Place("p2", "Ys", firstSeen: 9)]);

        model.Through(3).Places.Should().ContainSingle(p => p.Name == "Ravensmarch");
    }

    /// <summary>
    /// Telling a reader that an inn is in a city they have never heard of is
    /// telling them about the city.
    /// </summary>
    [Fact]
    public void A_container_the_reader_has_not_reached_is_cleared_rather_than_left_dangling()
    {
        var model = Model(places:
        [
            Place("p1", "The Gate House", PlaceKind.Building, partOf: "p2", firstSeen: 0),
            Place("p2", "Ravensmarch", firstSeen: 9)
        ]);

        model.Through(3).Places.Should().ContainSingle().Which.PartOf.Should().BeEmpty();
    }
}
