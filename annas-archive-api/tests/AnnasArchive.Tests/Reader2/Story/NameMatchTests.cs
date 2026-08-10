using AnnasArchive.API.Reader2.Story;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// Which names are mechanically the same person.
///
/// <para>The narrowness is the feature. Everything this says yes to is merged
/// without anybody looking, so the cases it must <i>refuse</i> matter more than
/// the ones it catches — a false positive fuses two characters into a story the
/// reader cannot tell is wrong.</para>
/// </summary>
public class NameMatchTests
{
    [Theory]
    [InlineData("Pierre", "pierre")]
    [InlineData("BEZUKHOV", "Bezukhov")]
    public void Case_is_not_a_different_person(string a, string b) =>
        NameMatch.Same(a, b).Should().BeTrue();

    [Theory]
    [InlineData("Bezúkhov", "Bezukhov")]
    [InlineData("Dólokhov", "Dolokhov")]
    [InlineData("Kutúzov", "Kutuzov")]
    public void Diacritics_are_not_a_different_person(string a, string b) =>
        NameMatch.Same(a, b).Should().BeTrue();

    /// <summary>
    /// A patronymic is dropped in half the places a character is named, which is
    /// what makes a long Russian novel the hardest case this has to survive.
    /// </summary>
    [Theory]
    [InlineData("Pyotr Kirillovich Bezukhov", "Pyotr Bezukhov")]
    [InlineData("Natalya Ilyinichna Rostova", "Natalya Rostova")]
    [InlineData("Andrei Nikolayevich Bolkonsky", "Andrei Bolkonsky")]
    public void A_patronymic_between_two_names_is_droppable(string full, string shorter) =>
        NameMatch.Same(full, shorter).Should().BeTrue();

    [Theory]
    [InlineData("Count Bezukhov", "Bezukhov")]
    [InlineData("Prince Andrei", "Andrei")]
    [InlineData("General Kutuzov", "Kutuzov")]
    [InlineData("Colonel Bolkonsky", "General Bolkonsky")]
    public void A_title_is_not_part_of_the_name(string titled, string plain) =>
        NameMatch.Same(titled, plain).Should().BeTrue();

    /// <summary>
    /// The refusals. Every one of these is a pair a looser rule would fuse, and
    /// each is two people in the book it comes from.
    /// </summary>
    [Theory]
    [InlineData("Natalya Rostova", "Nikolai Rostov")]      // siblings
    [InlineData("Pyotr Bezukhov", "Pyotr Bagration")]      // a shared given name
    [InlineData("Prince Andrei", "Prince Vasili")]         // a shared title
    [InlineData("Anna Pavlovna", "Anna Mikhaylovna")]      // a shared given name and different patronymics
    [InlineData("Bolkonsky", "Bolkonskaya")]               // masculine and feminine surname forms
    public void Names_that_merely_look_alike_are_left_apart(string a, string b) =>
        NameMatch.Same(a, b).Should().BeFalse();

    /// <summary>
    /// A patronymic used as the name somebody is known by is not dropped. Only a
    /// patronymic sitting between two other names is, because that is the only
    /// position where dropping it cannot lose the person entirely.
    /// </summary>
    [Fact]
    public void A_patronymic_standing_alone_is_kept()
    {
        NameMatch.Same("Kirillovich", "").Should().BeFalse();
        NameMatch.Key("Kirillovich").Should().Be("kirillovich");
    }

    /// <summary>
    /// Some books name a character only by their rank. Stripping the title to
    /// nothing would lose them — but stopping at "the" would match them to every
    /// other "The …" in the book, which is the worse of the two.
    /// </summary>
    [Fact]
    public void A_name_that_is_nothing_but_a_title_keeps_it()
    {
        NameMatch.Key("The General").Should().Be("general");
        NameMatch.Same("The General", "the general").Should().BeTrue();
        NameMatch.Same("The General", "The Colonel").Should().BeFalse();
    }

    [Fact]
    public void Nothing_matches_nothing()
    {
        NameMatch.Same("", "").Should().BeFalse();
        NameMatch.Same(null, "Pierre").Should().BeFalse();
    }

    [Fact]
    public void An_actor_answers_to_every_name_it_has()
    {
        var actor = Cast.Actor("a1", "Pyotr Kirillovich Bezukhov", aliases: ["Pierre"]);

        NameMatch.Answers(actor, "Pierre").Should().BeTrue();
        NameMatch.Answers(actor, "Count Bezúkhov").Should().BeFalse("no name of theirs is 'Bezukhov' alone");
        NameMatch.Answers(actor, "Pyotr Bezukhov").Should().BeTrue();
        NameMatch.Answers(actor, "Nikolai Rostov").Should().BeFalse();
    }
}
