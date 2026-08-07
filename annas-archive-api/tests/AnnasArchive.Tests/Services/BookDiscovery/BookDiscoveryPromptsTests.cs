using AnnasArchive.API.Models;
using AnnasArchive.API.Services.BookDiscovery;

namespace AnnasArchive.Tests.Services.BookDiscovery;

/// <summary>
/// The prompts are the specification of what these endpoints return, so these
/// tests assert the rules that would be expensive to discover from a wrong
/// answer in production: that a URL query cannot invent titles, that the token
/// budget matches the size of the answer asked for, and that user input reaches
/// the model at all.
/// </summary>
public class BookDiscoveryPromptsTests
{
    [Fact]
    public void SuggestAuthorsAsksAboutTheTitleGiven()
    {
        var call = BookDiscoveryPrompts.SuggestAuthors("gpt-4o", "The Left Hand of Darkness");

        call.Model.Should().Be("gpt-4o");
        call.Endpoint.Should().Be("suggest-authors");
        call.UserPrompt.Should().Contain("The Left Hand of Darkness");
        call.IsRetry.Should().BeNull("this route has no retry leg, so the PerfLog tag is omitted");
    }

    [Fact]
    public void RelatedBooksBudgetsForAWholeBibliography()
    {
        var call = BookDiscoveryPrompts.RelatedBooks("gpt-4o", "Pandora's Star", "Peter F. Hamilton");

        call.UserPrompt.Should().Contain("Pandora's Star").And.Contain("Peter F. Hamilton");
        call.UserPrompt.Should().Contain("ALL known published titles");
        // The prompt asks for every title in every series with no ellipses; at
        // a smaller budget the model truncates mid-array and the answer parses
        // to a short list rather than failing visibly.
        call.MaxCompletionTokens.Should().Be(3500);
    }

    [Fact]
    public void BookSearchWithoutAUrlAsksForAFreshList()
    {
        var call = BookDiscoveryPrompts.BookSearch("gpt-5", "best cyberpunk novels", []);

        call.UserPrompt.Should().Contain("ExtractedTitles: None");
        call.UserPrompt.Should().Contain("otherwise return 10-25");
        call.MaxCompletionTokens.Should().Be(2000);
        call.IsRetry.Should().BeFalse("book-search has a retry leg, so the first call is tagged as not one");
    }

    [Fact]
    public void BookSearchWithAUrlForbidsInventingTitles()
    {
        // The whole point of a "get every book on this page" query is the
        // page's list. A plausible extra title is a wrong answer, not a bonus.
        var call = BookDiscoveryPrompts.BookSearch(
            "gpt-5", "https://example.com/best-of-2024", ["Dune", "Neuromancer"]);

        call.UserPrompt.Should().Contain("do not invent titles not present");
        call.UserPrompt.Should().Contain("- Dune").And.Contain("- Neuromancer");
        call.MaxCompletionTokens.Should().Be(6000, "a URL query returns many more books");
    }

    [Fact]
    public void BookSearchTightensThePerBookBudgetOnALongList()
    {
        var short_ = BookDiscoveryPrompts.BookSearch("gpt-5", "http://x/list", Titles(59));
        var long_ = BookDiscoveryPrompts.BookSearch("gpt-5", "http://x/list", Titles(60));

        short_.UserPrompt.Should().Contain("max 45 words");
        long_.UserPrompt.Should().Contain("max 24 words",
            "60 titles at 45 words each overruns the completion budget and the model truncates mid-array");
    }

    [Fact]
    public void BookSearchCapsTheRequestedCountAtTheNumberOfTitlesFound()
    {
        BookDiscoveryPrompts.BookSearch("gpt-5", "http://x/list", Titles(7))
            .UserPrompt.Should().Contain("return up to 7 books");

        BookDiscoveryPrompts.BookSearch("gpt-5", "http://x/list", Titles(40))
            .UserPrompt.Should().Contain("return up to 20 books");
    }

    [Fact]
    public void TheRetryPinsADifferentModelAndInsistsOnBooks()
    {
        var retry = BookDiscoveryPrompts.BookSearchRetry("books about ferrets", []);

        // The configured deep model already returned nothing; asking it again
        // identically is the one approach guaranteed not to help.
        retry.Model.Should().Be("gpt-4o");
        retry.IsRetry.Should().BeTrue();
        retry.UserPrompt.Should().Contain("You MUST return 10-20 books");
        retry.UserPrompt.Should().Contain("books about ferrets");
    }

    [Fact]
    public void MatchSeriesBooksSendsTheCandidatesAndTheOmnibusRule()
    {
        var request = new MatchSeriesBooksRequest(
            "Commonwealth Saga", "Peter F. Hamilton", "EPUB",
            [new BookWithCandidates("Pandora's Star", 1,
                [new CandidateBook("abc123", "Pandora's Star", ["Peter F. Hamilton"], "EPUB", "2 MB")])]);

        var call = BookDiscoveryPrompts.MatchSeriesBooks("gpt-4o", request);

        call.UserPrompt.Should().Contain("abc123", "the model picks a candidate by md5");
        call.UserPrompt.Should().Contain("Commonwealth Saga").And.Contain("EPUB");
        call.SystemPrompt.Should().Contain("AVOID: Omnibus editions");
        call.Temperature.Should().Be(0.2, "matching is a lookup, not a creative task");
    }

    [Fact]
    public void MatchSeriesBooksNamesTheUnknownsRatherThanSendingNull()
    {
        var request = new MatchSeriesBooksRequest(null, "Anonymous", null, [
            new BookWithCandidates("Beowulf", 1, [])
        ]);

        var call = BookDiscoveryPrompts.MatchSeriesBooks("gpt-4o", request);

        call.UserPrompt.Should().Contain("Unknown Series").And.Contain(@"Preferred Format: ""ANY""");
    }

    [Fact]
    public void GroupSearchResultsSendsIndicesInsteadOfHashes()
    {
        // Asking a model to echo back 32-char md5 hashes for 50+ books invites
        // silent transcription errors that would misfile or drop a book.
        var call = BookDiscoveryPrompts.GroupSearchResults("gpt-4o", [
            new GroupableBook("d41d8cd98f00b204e9800998ecf8427e", "Dune", ["Frank Herbert"], "EPUB", 1965),
            new GroupableBook("c4ca4238a0b923820dcc509a6f75849b", "Dune", ["Frank Herbert"], "PDF", 1965)
        ]);

        call.UserPrompt.Should().NotContain("d41d8cd98f00b204e9800998ecf8427e");
        call.UserPrompt.Should().Contain("\"index\": 0").And.Contain("\"index\": 1");
        call.UserPrompt.Should().Contain("Dune");
        call.SystemPrompt.Should().Contain("Format never matters for grouping");
        call.Temperature.Should().Be(0.1);
    }

    [Theory]
    [InlineData("https://example.com/list", true)]
    [InlineData("HTTP://EXAMPLE.COM", true)]
    [InlineData("read this: http://x.io/a", true)]
    [InlineData("best books about http servers", false)]
    [InlineData("", false)]
    public void RecognisesAUrlInTheQuery(string query, bool expected)
    {
        BookDiscoveryPrompts.ContainsUrl(query).Should().Be(expected);
    }

    private static List<string> Titles(int count) =>
        Enumerable.Range(1, count).Select(i => $"Book {i}").ToList();
}
