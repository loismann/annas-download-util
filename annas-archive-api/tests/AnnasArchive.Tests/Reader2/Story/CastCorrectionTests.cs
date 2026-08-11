using AnnasArchive.API.Reader2.Story;
using static AnnasArchive.Tests.Reader2.Story.Cast;

namespace AnnasArchive.Tests.Reader2.Story;

/// <summary>
/// The reader overruling the model.
///
/// <para>Laid over the stored model rather than written into it, and the tests
/// below are mostly about what that buys: a correction survives a rebuild, is
/// undone by deleting it, and never turns the record into something the
/// extraction never said.</para>
/// </summary>
public class CastCorrectionTests
{
    private static StoryModel Corrected(StoryModel model, params CastOverride[] entries) =>
        CastCorrections.Apply(model, new CastOverrides(entries));

    private static CastOverride Rename(string name, string to) =>
        new(NameMatch.Key(name), PreferredName: to);

    // ─── renaming ───────────────────────────────────────────────────────

    [Fact]
    public void A_reader_can_be_shown_the_short_name_instead_of_the_formal_one()
    {
        var model = Model([Actor("a1", "Finbar Charles Louis Griffin Jalgori-Tobu")]);

        var after = Corrected(model, Rename("Finbar Charles Louis Griffin Jalgori-Tobu", "Finn"));

        after.ById("a1").CanonicalName.Should().Be("Finn");
    }

    /// <summary>
    /// The name the model chose is still the name the book uses, still what the
    /// next chapter will say, and still how the correction finds them again.
    /// </summary>
    [Fact]
    public void The_name_the_model_chose_is_kept_as_an_alias_rather_than_thrown_away()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu")]);

        var after = Corrected(model, Rename("Finbar Jalgori-Tobu", "Finn"));

        after.ById("a1").Aliases.Should().Contain("Finbar Jalgori-Tobu");
    }

    [Fact]
    public void A_note_is_the_readers_own_and_sits_beside_the_models_dossier()
    {
        var model = Model([Actor("a1", "Finn")]);

        var after = Corrected(model, new CastOverride(NameMatch.Key("Finn"), Note: "the one who lied in ch 3"));

        after.ById("a1").ReaderNote.Should().Be("the one who lied in ch 3");
        after.ById("a1").Dossier.Should().BeEmpty("a note is not a dossier, and must not overwrite one");
    }

    // ─── the whole point: surviving a rebuild ───────────────────────────

    /// <summary>
    /// <b>The reason corrections are keyed on a name.</b> A rebuild admits actors
    /// afresh and numbers them in whatever order the chapters happen to report
    /// them, so a correction stored against <c>a1</c> would come back attached to
    /// whoever <c>a1</c> then was.
    /// </summary>
    [Fact]
    public void A_correction_lands_on_the_same_person_after_a_rebuild_renumbers_everybody()
    {
        var before = Model([Actor("a1", "Finbar Jalgori-Tobu"), Actor("a2", "Ellie")]);
        var correction = Rename("Finbar Jalgori-Tobu", "Finn");

        Corrected(before, correction).ById("a1").CanonicalName.Should().Be("Finn");

        // The same book, rebuilt: the same people, different ids and order.
        var rebuilt = Model([Actor("a1", "Ellie"), Actor("a2", "Finbar Jalgori-Tobu")]);

        Corrected(rebuilt, correction).ById("a2").CanonicalName.Should().Be("Finn");
        Corrected(rebuilt, correction).ById("a1").CanonicalName.Should().Be("Ellie");
    }

    [Fact]
    public void A_correction_finds_somebody_by_any_name_they_answer_to()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu", aliases: ["Finn"])]);

        // Keyed on the alias, because that is what they were called when it was made.
        var after = Corrected(model, new CastOverride(NameMatch.Key("Finn"), Note: "the heir"));

        after.ById("a1").ReaderNote.Should().Be("the heir");
    }

    [Fact]
    public void Removing_a_correction_puts_the_models_own_account_back()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu")]);
        var corrections = new CastOverrides([Rename("Finbar Jalgori-Tobu", "Finn")]);

        var undone = CastCorrections.Apply(model, corrections.Without(NameMatch.Key("Finbar Jalgori-Tobu")));

        undone.ById("a1").CanonicalName.Should().Be("Finbar Jalgori-Tobu");
        undone.ById("a1").Aliases.Should().BeEmpty();
    }

    [Fact]
    public void A_correction_about_nobody_in_this_book_changes_nothing()
    {
        var model = Model([Actor("a1", "Finn")]);

        Corrected(model, Rename("Somebody Else Entirely", "X")).Should().Be(model);
    }

    /// <summary>
    /// Two entries answering to one name is exactly the situation a correction
    /// would be used to fix — and applying it to the wrong one of two people is a
    /// wrong record the reader cannot see.
    /// </summary>
    [Fact]
    public void A_name_belonging_to_two_entries_corrects_neither()
    {
        var model = Model([Actor("a1", "Lord Valdier"), Actor("a2", "Valdier")]);

        var after = Corrected(model, new CastOverride(NameMatch.Key("Valdier"), Note: "a note"));

        after.Actors.Should().OnlyContain(a => a.ReaderNote == "");
    }

    // ─── consolidating ──────────────────────────────────────────────────

    [Fact]
    public void The_reader_can_say_two_entries_are_one_person()
    {
        var model = Model(
            [Actor("a1", "Lord Gahiji-Calder"), Actor("a2", "Gahiji"), Actor("a3", "Heba")],
            edges: [Edge("a2", "a3", "allied")]);

        var after = Corrected(model, new CastOverride(
            NameMatch.Key("Lord Gahiji-Calder"), SameAs: [NameMatch.Key("Gahiji")]));

        after.Actors.Should().HaveCount(2);
        after.ById("a1").Aliases.Should().Contain("Gahiji");
        after.Edges.Single().From.Should().Be("a1", "the absorbed entry's relationships come with them");
    }

    /// <summary>
    /// Non-destructive is the whole design: the stored model still holds both, so
    /// changing your mind is deleting a row rather than re-reading the book.
    /// </summary>
    [Fact]
    public void Undoing_a_consolidation_splits_them_again()
    {
        var model = Model([Actor("a1", "Lord Gahiji-Calder"), Actor("a2", "Gahiji")]);
        var corrections = new CastOverrides([
            new CastOverride(NameMatch.Key("Lord Gahiji-Calder"), SameAs: [NameMatch.Key("Gahiji")])
        ]);

        CastCorrections.Apply(model, corrections).Actors.Should().ContainSingle();
        CastCorrections.Apply(model, corrections.Without(NameMatch.Key("Lord Gahiji-Calder")))
            .Actors.Should().HaveCount(2);
    }

    [Fact]
    public void Renaming_and_consolidating_at_once_keeps_both()
    {
        var model = Model([Actor("a1", "Finbar Jalgori-Tobu"), Actor("a2", "The heir")]);

        var after = Corrected(model, new CastOverride(
            NameMatch.Key("Finbar Jalgori-Tobu"), PreferredName: "Finn",
            SameAs: [NameMatch.Key("The heir")]));

        after.Actors.Should().ContainSingle();
        after.Actors.Single().CanonicalName.Should().Be("Finn");
        after.Actors.Single().Aliases.Should().Contain("The heir");
    }

    // ─── the store ──────────────────────────────────────────────────────

    [Fact]
    public void An_emptied_correction_is_removed_rather_than_stored_saying_nothing()
    {
        var corrections = new CastOverrides([Rename("Finn", "F")])
            .With(new CastOverride(NameMatch.Key("Finn")));

        corrections.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Correcting_the_same_person_twice_replaces_rather_than_accumulates()
    {
        var corrections = new CastOverrides([])
            .With(Rename("Finn", "F"))
            .With(Rename("Finn", "Finny"));

        corrections.Entries.Should().ContainSingle().Which.PreferredName.Should().Be("Finny");
    }
}
