using AnnasArchive.Core.Helpers;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// <see cref="SafeFileName.ForReadablePathSegment"/> replaces a hand-rolled sanitiser
/// that lived on <c>AudiobookEnrichmentService</c>. That one had a good reason to exist
/// — a media library folder reads better with spaces than underscores — but it had been
/// written from scratch and was missing the traversal and control-character handling
/// the shared helper already had.
///
/// Its input is a catalogue lookup or a language model's answer, so that hardening is
/// the point, not decoration.
/// </summary>
public class SafeFileNameReadableSegmentTests
{
    // ─── The reason it differs from ForUserInput ──────────────────────────

    /// <summary>
    /// The whole justification for a separate method. A person browses these folders,
    /// and so does Audiobookshelf's scanner; <c>Nineteen Eighty_Four_ A Novel</c> is
    /// worse than the spaced form for both.
    /// </summary>
    [Fact]
    public void ReplacesInvalidCharactersWithSpacesRatherThanUnderscores()
    {
        SafeFileName.ForReadablePathSegment("Nineteen Eighty-Four: A Novel")
            .Should().Be("Nineteen Eighty-Four A Novel");
    }

    [Fact]
    public void CollapsesTheRunsOfSpacesThatSubstitutionCreates()
    {
        SafeFileName.ForReadablePathSegment("What? | Why? | When?")
            .Should().Be("What Why When");
    }

    /// <summary>
    /// An underscore the title genuinely contains is not something this method
    /// introduced, so it survives. This is why substitution has to happen before
    /// delegating rather than after — afterwards the two are indistinguishable.
    /// </summary>
    [Fact]
    public void KeepsAnUnderscoreTheTitleItselfContains()
    {
        SafeFileName.ForReadablePathSegment("File_Name: A Story").Should().Be("File_Name A Story");
    }

    [Fact]
    public void LeavesAnAlreadyCleanTitleAlone()
    {
        SafeFileName.ForReadablePathSegment("Dune (1965)").Should().Be("Dune (1965)");
    }

    // ─── The hardening the hand-rolled version was missing ────────────────

    /// <summary>
    /// The gap that mattered. Path separators were already handled, but a bare
    /// traversal segment was not — the old version returned an empty string for "..",
    /// which silently collapsed the path to its parent instead of creating a folder.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("....")]
    public void NeutralisesTraversalSegmentsInsteadOfReturningNothing(string input)
    {
        var result = SafeFileName.ForReadablePathSegment(input);

        result.Should().NotBeEmpty();
        result.Should().NotContain("..");
    }

    [Fact]
    public void StripsControlCharacters()
    {
        SafeFileName.ForReadablePathSegment("Dune\u0000\u0007 Messiah").Should().Be("Dune Messiah");
    }

    [Fact]
    public void FlattensAPathIntoASingleSegment()
    {
        SafeFileName.ForReadablePathSegment("/etc/passwd").Should().NotContain("/");
        SafeFileName.ForReadablePathSegment("C:\\Windows\\System32").Should().NotContain("\\");
    }

    /// <summary>
    /// A trailing dot or space is silently stripped by some filesystems, so a name
    /// carrying one would differ from the name actually on disk — and every later
    /// lookup by that name would miss.
    /// </summary>
    [Theory]
    [InlineData("Dune.")]
    [InlineData("Dune ")]
    [InlineData("Dune. . ")]
    public void LeavesNoTrailingDotOrSpace(string input)
    {
        SafeFileName.ForReadablePathSegment(input).Should().Be("Dune");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("...")]
    public void FallsBackRatherThanReturningAnEmptySegment(string? input)
    {
        SafeFileName.ForReadablePathSegment(input).Should().Be("untitled");
    }

    [Fact]
    public void UsesTheCallersFallbackWhenOneIsGiven()
    {
        SafeFileName.ForReadablePathSegment("", fallback: "Unknown Author")
            .Should().Be("Unknown Author");
    }

    [Fact]
    public void TruncatesToTheGivenLength()
    {
        var long_ = new string('a', 500);

        SafeFileName.ForReadablePathSegment(long_, maxLength: 50).Length.Should().BeLessThanOrEqualTo(50);
    }

    /// <summary>The shape the enrichment service actually builds.</summary>
    [Fact]
    public void ProducesTheAuthorAndTitleFoldersTheLibraryExpects()
    {
        SafeFileName.ForReadablePathSegment("Frank Herbert").Should().Be("Frank Herbert");
        SafeFileName.ForReadablePathSegment("Dune: Book One (1965)").Should().Be("Dune Book One (1965)");
    }
}
