using System.Globalization;
using AnnasArchive.API.Services.Library;

namespace AnnasArchive.Tests.Services.Library;

public class GoodreadsRatingParserTests
{
    [Fact]
    public void FromSearchResultText_ReadsTheAverageRating()
    {
        // This is the regression test for the escaping bug. The pattern used to be
        // written `@"...\\s*avg rating"` in a verbatim string, which the regex engine
        // read as "a literal backslash, then zero or more letter s" — so it could
        // never match real Goodreads text and this returned null every single time.
        GoodreadsRatingParser.FromSearchResultText("4.15 avg rating — 2,347 ratings")
            .Should().Be(4.15);
    }

    [Theory]
    [InlineData("4.15 avg rating", 4.15)]
    [InlineData("4.15avg rating", 4.15)]     // no space
    [InlineData("4.15   avg rating", 4.15)]  // several
    [InlineData("4 avg rating", 4.0)]
    [InlineData("4.15 AVG RATING", 4.15)]    // case-insensitive
    public void FromSearchResultText_ToleratesTheSpacingAndCasingGoodreadsUses(string text, double expected)
    {
        GoodreadsRatingParser.FromSearchResultText(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no rating here")]
    [InlineData("avg rating")]               // no number
    [InlineData("2,347 ratings")]            // a number, but not this one
    public void FromSearchResultText_ReturnsNullWhenThereIsNoAverageRating(string text)
    {
        GoodreadsRatingParser.FromSearchResultText(text).Should().BeNull();
    }

    [Fact]
    public void FromHtml_PrefersJsonLd()
    {
        const string html = """
            <script type="application/ld+json">{"ratingValue": "4.42"}</script>
            <span itemprop="ratingValue">3.10</span>
            <span>2.00 avg rating</span>
            """;

        GoodreadsRatingParser.FromHtml(html).Should().Be(4.42);
    }

    [Fact]
    public void FromHtml_FallsBackToTheMicrodataAttribute()
    {
        const string html = """
            <span itemprop="ratingValue">3.10</span>
            <span>2.00 avg rating</span>
            """;

        GoodreadsRatingParser.FromHtml(html).Should().Be(3.10);
    }

    [Fact]
    public void FromHtml_FallsBackToTheHumanReadableText()
    {
        // The third rung was also broken by the same escaping bug, so a page with
        // only the readable form silently produced no rating at all.
        GoodreadsRatingParser.FromHtml("<div>3.87 avg rating — 1,204 ratings</div>")
            .Should().Be(3.87);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>nothing useful</body></html>")]
    public void FromHtml_ReturnsNullWhenNoPatternMatches(string html)
    {
        GoodreadsRatingParser.FromHtml(html).Should().BeNull();
    }

    [Fact]
    public void Parsing_UsesInvariantCultureRegardlessOfTheServerLocale()
    {
        // A container set to a comma-decimal locale would otherwise read "4.15" as
        // 415. The server runs UTC/invariant today, but nothing enforces that.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            GoodreadsRatingParser.FromSearchResultText("4.15 avg rating").Should().Be(4.15);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
