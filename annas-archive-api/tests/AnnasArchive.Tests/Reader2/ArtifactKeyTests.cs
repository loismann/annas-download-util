using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Pins the §7.1 key table. These are the shapes the UNIQUE constraint relies
/// on, so a change here is a change to what can silently overwrite what.
/// </summary>
public class ArtifactKeyTests
{
    private static readonly BookRef Book = BookRef.Parse("a1b2c3d4e5f60718");
    private const string Lens = "fiction";

    [Fact]
    public void Lens_independent_kinds_are_stored_under_none()
    {
        ArtifactKey.ChapterIndex(Book).LensKey.Should().Be(ArtifactKinds.NoLens);
        ArtifactKey.ChapterLabels(Book).LensKey.Should().Be(ArtifactKinds.NoLens);
        ArtifactKey.Flashcards(Book).LensKey.Should().Be(ArtifactKinds.NoLens);
        ArtifactKey.ChunkBoundaries(Book, 3).LensKey.Should().Be(ArtifactKinds.NoLens);
    }

    [Fact]
    public void Book_scoped_kinds_use_the_chapter_and_ordinal_sentinels()
    {
        var key = ArtifactKey.StoryModel(Book, Lens);

        key.Chapter.Should().Be(ArtifactKey.NoChapter);
        key.Ordinal.Should().Be(ArtifactKey.NoOrdinal);
        key.Subkey.Should().Be(ArtifactKey.NoSubkey);
    }

    [Fact]
    public void Chapter_scoped_kinds_carry_the_chapter_but_no_ordinal()
    {
        var summary = ArtifactKey.ChapterSummary(Book, Lens, 7);

        summary.Chapter.Should().Be(7);
        summary.Ordinal.Should().Be(ArtifactKey.NoOrdinal);
    }

    [Fact]
    public void Section_kinds_carry_the_section_index_as_the_ordinal()
    {
        ArtifactKey.SectionSummary(Book, Lens, 7, 2).Ordinal.Should().Be(2);
        ArtifactKey.SectionVocab(Book, Lens, 7, 2).Ordinal.Should().Be(2);
    }

    [Fact]
    public void Passage_analysis_carries_the_word_offset_as_the_ordinal()
    {
        ArtifactKey.PassageAnalysis(Book, Lens, 7, 1450).Ordinal.Should().Be(1450);
    }

    [Fact]
    public void Learn_more_is_keyed_by_the_normalised_term()
    {
        var key = ArtifactKey.LearnMore(Book, Lens, "reification");

        key.Subkey.Should().Be("reification");
        key.Chapter.Should().Be(ArtifactKey.NoChapter);
    }

    /// <summary>
    /// Two book-scoped artifacts of one kind must be the same row, or the
    /// UNIQUE constraint would let a book accumulate several "the" story models.
    /// </summary>
    [Fact]
    public void The_same_book_scoped_artifact_requested_twice_is_one_key()
    {
        ArtifactKey.StoryModel(Book, Lens).Should().Be(ArtifactKey.StoryModel(Book, Lens));
        ArtifactKey.ChapterIndex(Book).Should().Be(ArtifactKey.ChapterIndex(Book));
    }

    /// <summary>The point of the lens column: both book types coexist per chapter.</summary>
    [Fact]
    public void The_same_chapter_under_two_lenses_is_two_keys()
    {
        ArtifactKey.ChapterSummary(Book, "fiction", 3)
            .Should().NotBe(ArtifactKey.ChapterSummary(Book, "military", 3));
    }

    [Fact]
    public void Different_kinds_at_the_same_position_are_different_keys()
    {
        ArtifactKey.SectionSummary(Book, Lens, 3, 0)
            .Should().NotBe(ArtifactKey.SectionVocab(Book, Lens, 3, 0));
    }

    /// <summary>
    /// A lens-scoped artifact stored under "none" would collide with the
    /// lens-independent artifact of the same kind and silently overwrite it.
    /// </summary>
    [Fact]
    public void A_lens_scoped_key_refuses_the_reserved_none_lens()
    {
        var act = () => ArtifactKey.ChapterSummary(Book, ArtifactKinds.NoLens, 1);
        act.Should().Throw<ArgumentException>().WithMessage("*reserved*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_lens_scoped_key_requires_a_lens(string? lensKey)
    {
        var act = () => ArtifactKey.ChapterSummary(Book, lensKey!, 1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Negative_chapters_and_ordinals_are_rejected_so_they_cannot_masquerade_as_sentinels()
    {
        var negativeChapter = () => ArtifactKey.ChapterSummary(Book, Lens, -1);
        var negativeSection = () => ArtifactKey.SectionSummary(Book, Lens, 0, -1);

        negativeChapter.Should().Throw<ArgumentOutOfRangeException>();
        negativeSection.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Learn_more_requires_a_term()
    {
        var act = () => ArtifactKey.LearnMore(Book, Lens, "  ");
        act.Should().Throw<ArgumentException>();
    }
}

public class ArtifactKindTests
{
    [Fact]
    public void Every_kind_has_a_wire_name_and_round_trips()
    {
        foreach (var kind in ArtifactKinds.All)
            ArtifactKinds.Parse(kind.Wire()).Should().Be(kind);
    }

    [Fact]
    public void Wire_names_are_unique()
    {
        var names = ArtifactKinds.All.Select(k => k.Wire()).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Wire_names_are_kebab_case_so_the_column_stays_readable()
    {
        ArtifactKinds.All.Select(k => k.Wire())
            .Should().OnlyContain(n => n == n.ToLowerInvariant() && !n.Contains(' '));
    }

    [Fact]
    public void No_kind_collides_with_the_reserved_lens_sentinel()
    {
        ArtifactKinds.All.Select(k => k.Wire()).Should().NotContain(ArtifactKinds.NoLens);
    }

    [Fact]
    public void An_unknown_wire_name_does_not_parse()
    {
        ArtifactKinds.TryParse("not-a-kind", out _).Should().BeFalse();
    }
}
