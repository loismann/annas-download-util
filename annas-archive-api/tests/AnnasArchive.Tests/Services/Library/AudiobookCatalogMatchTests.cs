using System.Text.Json.Nodes;
using AnnasArchive.API.Services.Library;
using FluentAssertions;

namespace AnnasArchive.Tests.Services.Library;

/// <summary>
/// The rule that decides whether an Audible edition is already in Audiobookshelf.
/// Every case below is a real pairing observed on the live library on 2026-08-03,
/// where the previous strict token-set rule matched none of the first three and so
/// left five freshly imported books showing as "not downloading yet" on the library
/// page while the search page offered to play them.
/// </summary>
public sealed class AudiobookCatalogMatchTests
{
    private static JsonObject Metadata(string title, string author) =>
        new() { ["title"] = title, ["authorName"] = author };

    [Theory]
    [InlineData("Judas Unchained", "Commonwealth Saga 2 - Judas Unchained")]      // series prefix
    [InlineData("Misspent Youth", "Misspent Youth [Unabridged]")]                 // edition suffix
    [InlineData("Pandora's Star", "Commonwealth Saga Book 1: Pandora's Star")]    // both
    [InlineData("Misspent Youth", "Misspent Youth")]                              // exact
    public void Matches_WhenAudiobookshelfDecoratesTheTitle(string wanted, string catalogued) =>
        AudiobookCatalogMatch
            .TitleAndAuthorMatch(wanted, ["Peter F. Hamilton"], Metadata(catalogued, "Peter F. Hamilton"))
            .Should().BeTrue();

    /// <summary>Containment is only safe while the author still has to agree.</summary>
    [Fact]
    public void DoesNotMatch_WhenTheAuthorDisagrees() =>
        AudiobookCatalogMatch
            .TitleAndAuthorMatch("Judas Unchained", ["Peter F. Hamilton"],
                Metadata("Commonwealth Saga 2 - Judas Unchained", "Steve Perry"))
            .Should().BeFalse();

    /// <summary>The containment trap: a one-word title sits inside a different book
    /// by the same author, so it is refused outright rather than matched.</summary>
    [Fact]
    public void DoesNotMatch_ASingleWordTitleInsideALongerOne() =>
        AudiobookCatalogMatch
            .TitleAndAuthorMatch("Exodus", ["Peter F. Hamilton"],
                Metadata("Exodus: The Archimedes Engine", "Peter F. Hamilton"))
            .Should().BeFalse();

    /// <summary>A title the catalogue only partly carries is still a miss — the
    /// decoration may be added, never dropped.</summary>
    [Fact]
    public void DoesNotMatch_WhenTheCatalogueTitleIsMissingWords() =>
        AudiobookCatalogMatch
            .TitleAndAuthorMatch("Star Wars: The Jedi Academy: Dark Apprentice", ["Kevin Anderson"],
                Metadata("Star Wars: The Jedi Academy: Champions of the Force", "Kevin Anderson"))
            .Should().BeFalse();

    [Fact]
    public void MissingItems_AreNeverOffered()
    {
        AudiobookCatalogMatch.IsMissing(new JsonObject { ["isMissing"] = true }).Should().BeTrue();
        AudiobookCatalogMatch.IsMissing(
            new JsonObject { ["media"] = new JsonObject { ["isMissing"] = true } }).Should().BeTrue();
        AudiobookCatalogMatch.IsMissing(new JsonObject { ["isMissing"] = false }).Should().BeFalse();
    }
}
