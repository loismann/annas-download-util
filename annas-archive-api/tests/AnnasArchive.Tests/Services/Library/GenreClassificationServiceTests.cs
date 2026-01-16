using AnnasArchive.API.Services.Library;
using Xunit;

namespace AnnasArchive.Tests.Services.Library;

public class GenreClassificationServiceTests
{
    private readonly GenreClassificationService _service = new();

    #region ClassifyGenre Tests

    [Fact]
    public void ClassifyGenre_WithNullSubjects_ReturnsUncategorized()
    {
        var result = _service.ClassifyGenre(null);
        Assert.Equal("Uncategorized", result);
    }

    [Fact]
    public void ClassifyGenre_WithEmptySubjects_ReturnsUncategorized()
    {
        var result = _service.ClassifyGenre(Array.Empty<string>());
        Assert.Equal("Uncategorized", result);
    }

    [Fact]
    public void ClassifyGenre_WithSciFiKeywords_ReturnsScienceFiction()
    {
        var subjects = new[] { "science fiction", "space opera", "aliens" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Science Fiction", result);
    }

    [Fact]
    public void ClassifyGenre_WithFantasyKeywords_ReturnsFantasy()
    {
        var subjects = new[] { "fantasy", "magic", "dragons" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Fantasy", result);
    }

    [Fact]
    public void ClassifyGenre_WithMysteryKeywords_ReturnsMysteryDetective()
    {
        var subjects = new[] { "mystery", "detective", "murder" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Mystery & Detective", result);
    }

    [Fact]
    public void ClassifyGenre_WithThrillerKeywords_ReturnsThriller()
    {
        var subjects = new[] { "thriller", "suspense", "espionage" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Thriller", result);
    }

    [Fact]
    public void ClassifyGenre_WithRomanceKeywords_ReturnsRomance()
    {
        var subjects = new[] { "romance", "love story", "relationships" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Romance", result);
    }

    [Fact]
    public void ClassifyGenre_WithHistoricalFictionKeywords_ReturnsHistoricalFiction()
    {
        var subjects = new[] { "historical fiction", "world war", "victorian" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Historical Fiction", result);
    }

    [Fact]
    public void ClassifyGenre_WithHorrorKeywords_ReturnsHorror()
    {
        var subjects = new[] { "horror", "vampire", "ghost" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Horror", result);
    }

    [Fact]
    public void ClassifyGenre_WithBiographyKeywords_ReturnsBiographyMemoir()
    {
        var subjects = new[] { "biography", "memoir", "autobiography" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Biography & Memoir", result);
    }

    [Fact]
    public void ClassifyGenre_WithScienceKeywords_ReturnsScienceTechnology()
    {
        var subjects = new[] { "science", "physics", "astronomy" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Science & Technology", result);
    }

    [Fact]
    public void ClassifyGenre_WithPhilosophyKeywords_ReturnsPhilosophy()
    {
        var subjects = new[] { "philosophy", "ethics", "metaphysics" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Philosophy", result);
    }

    [Fact]
    public void ClassifyGenre_ExactMatchScoresHigherThanContains()
    {
        // "fantasy" exact match should score higher than a subject that just contains it
        var subjects = new[] { "fantasy" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Fantasy", result);
    }

    [Fact]
    public void ClassifyGenre_WithMixedGenres_ReturnsHighestScoringGenre()
    {
        // Multiple sci-fi keywords should outweigh single fantasy keyword
        var subjects = new[] { "science fiction", "space", "aliens", "future", "fantasy" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Science Fiction", result);
    }

    [Fact]
    public void ClassifyGenre_IsCaseInsensitive()
    {
        var subjects = new[] { "SCIENCE FICTION", "Space Opera" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Science Fiction", result);
    }

    [Fact]
    public void ClassifyGenre_WithNoMatchingKeywords_ReturnsUncategorized()
    {
        var subjects = new[] { "random", "unrelated", "words" };
        var result = _service.ClassifyGenre(subjects);
        Assert.Equal("Uncategorized", result);
    }

    #endregion

    #region ExtractTags Tests

    [Fact]
    public void ExtractTags_WithNullSubjects_ReturnsEmptyArray()
    {
        var result = _service.ExtractTags(null, "Fantasy");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractTags_WithEmptySubjects_ReturnsEmptyArray()
    {
        var result = _service.ExtractTags(Array.Empty<string>(), "Fantasy");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractTags_ExcludesPrimaryGenre()
    {
        var subjects = new[] { "Fantasy", "magic", "dragons" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.DoesNotContain("Fantasy", result);
    }

    [Fact]
    public void ExtractTags_ExcludesStopWords()
    {
        var subjects = new[] { "fiction", "general", "book", "magic", "dragons" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.DoesNotContain("fiction", result);
        Assert.DoesNotContain("general", result);
        Assert.DoesNotContain("book", result);
    }

    [Fact]
    public void ExtractTags_ExcludesFictitiousCharacter()
    {
        var subjects = new[] { "magic", "fictitious character", "dragons" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.DoesNotContain("fictitious character", result);
    }

    [Fact]
    public void ExtractTags_ExcludesSingleCharacterTags()
    {
        var subjects = new[] { "a", "magic", "b", "dragons" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.DoesNotContain("a", result);
        Assert.DoesNotContain("b", result);
    }

    [Fact]
    public void ExtractTags_ExcludesNumericTags()
    {
        var subjects = new[] { "2023", "magic", "1984", "dragons" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.DoesNotContain("2023", result);
        Assert.DoesNotContain("1984", result);
    }

    [Fact]
    public void ExtractTags_RespectsLimit()
    {
        var subjects = new[] { "magic", "dragons", "wizards", "elves", "dwarves", "orcs", "goblins" };
        var result = _service.ExtractTags(subjects, "Fantasy", limit: 3);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void ExtractTags_DefaultLimitIsFive()
    {
        var subjects = new[] { "magic", "dragons", "wizards", "elves", "dwarves", "orcs", "goblins" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void ExtractTags_ReturnsValidTags()
    {
        var subjects = new[] { "magic", "dragons", "wizards" };
        var result = _service.ExtractTags(subjects, "Fantasy");
        Assert.Contains("magic", result);
        Assert.Contains("dragons", result);
        Assert.Contains("wizards", result);
    }

    [Fact]
    public void ExtractTags_IsCaseInsensitiveForPrimaryGenre()
    {
        var subjects = new[] { "FANTASY", "magic", "dragons" };
        var result = _service.ExtractTags(subjects, "fantasy");
        Assert.DoesNotContain("FANTASY", result);
    }

    #endregion
}
