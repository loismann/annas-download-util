using AnnasArchive.API.Helpers;
using HtmlAgilityPack;
using Xunit;

namespace AnnasArchive.Tests.Helpers;

public class BookTitleExtractionHelpersTests
{
    #region ExtractUrls Tests

    [Fact]
    public void ExtractUrls_ReturnsEmpty_WhenQueryIsNull()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUrls_ReturnsEmpty_WhenQueryIsEmpty()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUrls_ReturnsEmpty_WhenNoUrlsInQuery()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("This is just plain text without URLs");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUrls_ExtractsHttpUrl()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("Check out http://example.com for more");
        Assert.Single(result);
        Assert.Equal("http://example.com", result[0]);
    }

    [Fact]
    public void ExtractUrls_ExtractsHttpsUrl()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("Check out https://example.com for more");
        Assert.Single(result);
        Assert.Equal("https://example.com", result[0]);
    }

    [Fact]
    public void ExtractUrls_ExtractsMultipleUrls()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("Visit https://example.com and http://test.org");
        Assert.Equal(2, result.Count);
        Assert.Contains("https://example.com", result);
        Assert.Contains("http://test.org", result);
    }

    [Fact]
    public void ExtractUrls_TrimsTrailingPunctuation()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("Visit https://example.com).");
        Assert.Single(result);
        Assert.Equal("https://example.com", result[0]);
    }

    [Fact]
    public void ExtractUrls_RemovesDuplicates()
    {
        var result = BookTitleExtractionHelpers.ExtractUrls("Visit https://example.com and https://example.com again");
        Assert.Single(result);
    }

    #endregion

    #region CleanBookTitle Tests

    [Fact]
    public void CleanBookTitle_ReturnsEmpty_WhenNull()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle(null!);
        Assert.Equal("", result);
    }

    [Fact]
    public void CleanBookTitle_TrimsWhitespace()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("  The Great Gatsby  ");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_NormalizesMultipleSpaces()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("The   Great    Gatsby");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_RemovesLeadingNumbers()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("1. The Great Gatsby");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_RemovesLeadingBullets()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("• The Great Gatsby");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_SplitsOnByAuthor()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("The Great Gatsby by F. Scott Fitzgerald");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_TrimsQuotes()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("\"The Great Gatsby\"");
        Assert.Equal("The Great Gatsby", result);
    }

    [Fact]
    public void CleanBookTitle_DecodesHtmlEntities()
    {
        var result = BookTitleExtractionHelpers.CleanBookTitle("The &amp; Great Gatsby");
        Assert.Equal("The & Great Gatsby", result);
    }

    #endregion

    #region IsReasonableTitle Tests

    [Fact]
    public void IsReasonableTitle_ReturnsFalse_WhenNull()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle(null);
        Assert.False(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsFalse_WhenEmpty()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle("");
        Assert.False(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsFalse_WhenTooShort()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle("A");
        Assert.False(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsFalse_WhenTooLong()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle(new string('A', 121));
        Assert.False(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsFalse_WhenNoLetters()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle("123456");
        Assert.False(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsTrue_ForValidTitle()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle("The Great Gatsby");
        Assert.True(result);
    }

    [Fact]
    public void IsReasonableTitle_ReturnsTrue_ForMinimumValidTitle()
    {
        var result = BookTitleExtractionHelpers.IsReasonableTitle("AB");
        Assert.True(result);
    }

    #endregion

    #region LooksLikeNavigation Tests

    [Fact]
    public void LooksLikeNavigation_ReturnsTrue_ForRelatedPosts()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("Related Posts");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNavigation_ReturnsTrue_ForReadMore()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("Read More");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNavigation_ReturnsTrue_ForSubscribe()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("Subscribe to our newsletter");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNavigation_ReturnsTrue_ForCategories()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("Categories");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNavigation_ReturnsTrue_ForAdvertisement()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("Advertisement");
        Assert.True(result);
    }

    [Fact]
    public void LooksLikeNavigation_ReturnsFalse_ForBookTitle()
    {
        var result = BookTitleExtractionHelpers.LooksLikeNavigation("The Great Gatsby");
        Assert.False(result);
    }

    #endregion

    #region IsNavigationNode Tests

    [Fact]
    public void IsNavigationNode_ReturnsTrue_ForNavElement()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<nav>Navigation</nav>");
        var node = doc.DocumentNode.SelectSingleNode("//nav");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsNavigationNode_ReturnsTrue_ForHeaderElement()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<header>Header</header>");
        var node = doc.DocumentNode.SelectSingleNode("//header");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsNavigationNode_ReturnsTrue_ForFooterElement()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<footer>Footer</footer>");
        var node = doc.DocumentNode.SelectSingleNode("//footer");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsNavigationNode_ReturnsTrue_ForNavClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"nav-menu\">Menu</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsNavigationNode_ReturnsTrue_ForSidebarClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"sidebar\">Sidebar</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsNavigationNode_ReturnsFalse_ForContentDiv()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"content\">Content</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsNavigationNode(node);
        Assert.False(result);
    }

    #endregion

    #region IsBoilerplateNode Tests

    [Fact]
    public void IsBoilerplateNode_ReturnsTrue_ForNavClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"navigation\">Nav</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsBoilerplateNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsBoilerplateNode_ReturnsTrue_ForSidebarClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"sidebar\">Sidebar</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsBoilerplateNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsBoilerplateNode_ReturnsTrue_ForAdvertClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"advert\">Ad</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsBoilerplateNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsBoilerplateNode_ReturnsTrue_ForNewsletterClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"newsletter\">Subscribe</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsBoilerplateNode(node);
        Assert.True(result);
    }

    [Fact]
    public void IsBoilerplateNode_ReturnsFalse_ForArticleClass()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<div class=\"article-content\">Article</div>");
        var node = doc.DocumentNode.SelectSingleNode("//div");

        var result = BookTitleExtractionHelpers.IsBoilerplateNode(node);
        Assert.False(result);
    }

    #endregion

    #region HasNavigationAncestor Tests

    [Fact]
    public void HasNavigationAncestor_ReturnsTrue_WhenParentIsNav()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<nav><span>Link</span></nav>");
        var node = doc.DocumentNode.SelectSingleNode("//span");

        var result = BookTitleExtractionHelpers.HasNavigationAncestor(node);
        Assert.True(result);
    }

    [Fact]
    public void HasNavigationAncestor_ReturnsTrue_WhenGrandparentIsNav()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<nav><div><span>Link</span></div></nav>");
        var node = doc.DocumentNode.SelectSingleNode("//span");

        var result = BookTitleExtractionHelpers.HasNavigationAncestor(node);
        Assert.True(result);
    }

    [Fact]
    public void HasNavigationAncestor_ReturnsFalse_WhenNoNavAncestor()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<article><div><span>Content</span></div></article>");
        var node = doc.DocumentNode.SelectSingleNode("//span");

        var result = BookTitleExtractionHelpers.HasNavigationAncestor(node);
        Assert.False(result);
    }

    #endregion
}
