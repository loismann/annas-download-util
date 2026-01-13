using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class ChapterLabelerTests
{
    #region IsFrontMatterTitle Tests

    [Fact]
    public void IsFrontMatterTitle_ReturnsFalse_WhenEmpty()
    {
        Assert.False(ChapterLabeler.IsFrontMatterTitle(""));
        Assert.False(ChapterLabeler.IsFrontMatterTitle("   "));
    }

    [Theory]
    [InlineData("table of contents")]
    [InlineData("contents")]
    [InlineData("preface")]
    [InlineData("foreword")]
    [InlineData("introduction")]
    [InlineData("acknowledgments")]
    [InlineData("bibliography")]
    [InlineData("index")]
    [InlineData("glossary")]
    [InlineData("appendix")]
    [InlineData("dedication")]
    [InlineData("copyright")]
    [InlineData("prologue")]
    [InlineData("epilogue")]
    public void IsFrontMatterTitle_ReturnsTrue_ForFrontMatterKeywords(string title)
    {
        Assert.True(ChapterLabeler.IsFrontMatterTitle(title));
    }

    [Fact]
    public void IsFrontMatterTitle_ReturnsFalse_ForRegularChapter()
    {
        Assert.False(ChapterLabeler.IsFrontMatterTitle("the adventure begins"));
        Assert.False(ChapterLabeler.IsFrontMatterTitle("chapter one"));
    }

    #endregion

    #region IsStructuralHeading Tests

    [Theory]
    [InlineData("part one")]
    [InlineData("book two")]
    [InlineData("section three")]
    [InlineData("volume 1")]
    [InlineData("act i")]
    [InlineData("interlude")]
    public void IsStructuralHeading_ReturnsTrue_ForStructuralKeywords(string title)
    {
        Assert.True(ChapterLabeler.IsStructuralHeading(title));
    }

    [Fact]
    public void IsStructuralHeading_ReturnsFalse_WhenEmpty()
    {
        Assert.False(ChapterLabeler.IsStructuralHeading(""));
        Assert.False(ChapterLabeler.IsStructuralHeading("   "));
    }

    [Fact]
    public void IsStructuralHeading_ReturnsFalse_ForRegularTitle()
    {
        Assert.False(ChapterLabeler.IsStructuralHeading("the dark forest"));
    }

    #endregion

    #region HasChapterIndicator Tests

    [Fact]
    public void HasChapterIndicator_ReturnsFalse_WhenEmpty()
    {
        Assert.False(ChapterLabeler.HasChapterIndicator(""));
        Assert.False(ChapterLabeler.HasChapterIndicator("   "));
    }

    [Theory]
    [InlineData("Chapter 1")]
    [InlineData("CHAPTER ONE")]
    [InlineData("chapter 15: the beginning")]
    public void HasChapterIndicator_ReturnsTrue_ForChapterWord(string title)
    {
        Assert.True(ChapterLabeler.HasChapterIndicator(title));
    }

    [Theory]
    [InlineData("1. Introduction")]
    [InlineData("2: The Story")]
    [InlineData("3 - Beginning")]
    public void HasChapterIndicator_ReturnsTrue_ForLeadingNumber(string title)
    {
        Assert.True(ChapterLabeler.HasChapterIndicator(title));
    }

    [Theory]
    [InlineData("i. First")]
    [InlineData("iv: Fourth")]
    [InlineData("xii - Twelfth")]
    public void HasChapterIndicator_ReturnsTrue_ForRomanNumerals(string title)
    {
        Assert.True(ChapterLabeler.HasChapterIndicator(title));
    }

    [Fact]
    public void HasChapterIndicator_ReturnsFalse_ForPlainTitle()
    {
        Assert.False(ChapterLabeler.HasChapterIndicator("The Beginning"));
        Assert.False(ChapterLabeler.HasChapterIndicator("Prologue"));
    }

    #endregion

    #region IsLikelyMainContent Tests

    [Fact]
    public void IsLikelyMainContent_ReturnsTrue_WhenWordCountOver350()
    {
        var chapter = new FlatChapter(1, "Test", 1, "content", 400);
        Assert.True(ChapterLabeler.IsLikelyMainContent(chapter));
    }

    [Fact]
    public void IsLikelyMainContent_ReturnsTrue_WhenLevel0AndWordCountOver200()
    {
        var chapter = new FlatChapter(1, "Test", 0, "content", 250);
        Assert.True(ChapterLabeler.IsLikelyMainContent(chapter));
    }

    [Fact]
    public void IsLikelyMainContent_ReturnsFalse_WhenShortContent()
    {
        var chapter = new FlatChapter(1, "Test", 1, "content", 100);
        Assert.False(ChapterLabeler.IsLikelyMainContent(chapter));
    }

    #endregion

    #region IsLikelyTableOfContents Tests

    [Fact]
    public void IsLikelyTableOfContents_ReturnsFalse_WhenEmpty()
    {
        Assert.False(ChapterLabeler.IsLikelyTableOfContents(""));
        Assert.False(ChapterLabeler.IsLikelyTableOfContents("   "));
    }

    [Fact]
    public void IsLikelyTableOfContents_ReturnsTrue_WhenContainsTableOfContents()
    {
        Assert.True(ChapterLabeler.IsLikelyTableOfContents("Table of Contents\nChapter 1\nChapter 2"));
    }

    [Fact]
    public void IsLikelyTableOfContents_ReturnsFalse_ForShortContent()
    {
        Assert.False(ChapterLabeler.IsLikelyTableOfContents("Short text"));
    }

    #endregion

    #region BuildMainLabel Tests

    [Fact]
    public void BuildMainLabel_ReturnsChapterOnly_WhenTitleEmpty()
    {
        Assert.Equal("Chapter 1", ChapterLabeler.BuildMainLabel(1, ""));
        Assert.Equal("Chapter 5", ChapterLabeler.BuildMainLabel(5, "   "));
    }

    [Fact]
    public void BuildMainLabel_ReturnsChapterWithTitle_WhenTitleProvided()
    {
        Assert.Equal("Chapter 1: The Beginning", ChapterLabeler.BuildMainLabel(1, "The Beginning"));
    }

    [Fact]
    public void BuildMainLabel_CleansChapterPrefix_FromTitle()
    {
        Assert.Equal("Chapter 1: The Beginning", ChapterLabeler.BuildMainLabel(1, "Chapter 1: The Beginning"));
    }

    #endregion

    #region BuildNonMainLabel Tests

    [Fact]
    public void BuildNonMainLabel_ReturnsFrontMatter_WhenTitleEmpty()
    {
        Assert.Equal("i. Front matter", ChapterLabeler.BuildNonMainLabel(1, ""));
    }

    [Fact]
    public void BuildNonMainLabel_ReturnsRomanNumeral_WithTitle()
    {
        Assert.Equal("i. Preface", ChapterLabeler.BuildNonMainLabel(1, "Preface"));
        Assert.Equal("ii. Acknowledgments", ChapterLabeler.BuildNonMainLabel(2, "Acknowledgments"));
    }

    #endregion

    #region CleanChapterTitle Tests

    [Fact]
    public void CleanChapterTitle_ReturnsEmpty_WhenEmpty()
    {
        Assert.Equal("", ChapterLabeler.CleanChapterTitle(""));
        Assert.Equal("", ChapterLabeler.CleanChapterTitle("   "));
    }

    [Fact]
    public void CleanChapterTitle_RemovesChapterPrefix()
    {
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("Chapter 1: The Beginning"));
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("Chap. 5 The Beginning"));
    }

    [Fact]
    public void CleanChapterTitle_RemovesLeadingNumbers()
    {
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("1. The Beginning"));
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("15: The Beginning"));
    }

    [Fact]
    public void CleanChapterTitle_RemovesRomanNumerals()
    {
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("i. The Beginning"));
        Assert.Equal("The Beginning", ChapterLabeler.CleanChapterTitle("xii: The Beginning"));
    }

    #endregion

    #region CleanNonChapterTitle Tests

    [Fact]
    public void CleanNonChapterTitle_ReturnsEmpty_WhenEmpty()
    {
        Assert.Equal("", ChapterLabeler.CleanNonChapterTitle(""));
        Assert.Equal("", ChapterLabeler.CleanNonChapterTitle("   "));
    }

    [Fact]
    public void CleanNonChapterTitle_NormalizesWhitespace()
    {
        Assert.Equal("About The Author", ChapterLabeler.CleanNonChapterTitle("About  The   Author"));
    }

    #endregion

    #region NormalizeTitle Tests

    [Fact]
    public void NormalizeTitle_ConvertsToLowercase()
    {
        Assert.Equal("the beginning", ChapterLabeler.NormalizeTitle("The Beginning"));
    }

    [Fact]
    public void NormalizeTitle_NormalizesWhitespace()
    {
        Assert.Equal("the beginning", ChapterLabeler.NormalizeTitle("  The   Beginning  "));
    }

    #endregion

    #region ToRoman Tests

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(5, "V")]
    [InlineData(9, "IX")]
    [InlineData(10, "X")]
    [InlineData(40, "XL")]
    [InlineData(50, "L")]
    [InlineData(90, "XC")]
    [InlineData(100, "C")]
    [InlineData(400, "CD")]
    [InlineData(500, "D")]
    [InlineData(900, "CM")]
    [InlineData(1000, "M")]
    public void ToRoman_ConvertsCorrectly(int number, string expected)
    {
        Assert.Equal(expected, ChapterLabeler.ToRoman(number));
    }

    [Fact]
    public void ToRoman_HandlesComplexNumbers()
    {
        Assert.Equal("XIV", ChapterLabeler.ToRoman(14));
        Assert.Equal("XXIII", ChapterLabeler.ToRoman(23));
        Assert.Equal("MCMXCIX", ChapterLabeler.ToRoman(1999));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ToRoman_ReturnsLowercaseI_ForZeroOrNegative(int number)
    {
        Assert.Equal("i", ChapterLabeler.ToRoman(number));
    }

    #endregion

    #region LabelChapters Integration Tests

    [Fact]
    public void LabelChapters_LabelsMainChaptersCorrectly()
    {
        var chapters = new List<FlatChapter>
        {
            new(1, "Chapter 1: Beginning", 0, "content", 500),
            new(2, "Chapter 2: Middle", 0, "content", 500),
            new(3, "Chapter 3: End", 0, "content", 500)
        };

        var result = ChapterLabeler.LabelChapters(chapters);

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.True(r.IsMainChapter));
        Assert.Equal("Chapter 1: Beginning", result[0].DisplayLabel);
        Assert.Equal("Chapter 2: Middle", result[1].DisplayLabel);
        Assert.Equal("Chapter 3: End", result[2].DisplayLabel);
    }

    [Fact]
    public void LabelChapters_LabelsFrontMatterCorrectly()
    {
        var chapters = new List<FlatChapter>
        {
            new(1, "Preface", 0, "content", 100),
            new(2, "Introduction", 0, "content", 100),
            new(3, "Chapter 1", 0, "content", 500)
        };

        var result = ChapterLabeler.LabelChapters(chapters);

        Assert.Equal(3, result.Count);
        Assert.False(result[0].IsMainChapter);
        Assert.False(result[1].IsMainChapter);
        Assert.True(result[2].IsMainChapter);
        Assert.StartsWith("i.", result[0].DisplayLabel);
        Assert.StartsWith("ii.", result[1].DisplayLabel);
        Assert.StartsWith("Chapter 1", result[2].DisplayLabel);
    }

    #endregion
}
