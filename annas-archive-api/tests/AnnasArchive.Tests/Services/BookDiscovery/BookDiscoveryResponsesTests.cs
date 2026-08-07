using AnnasArchive.API.Services.BookDiscovery;
using AnnasArchive.Core.Services;

namespace AnnasArchive.Tests.Services.BookDiscovery;

/// <summary>
/// These parsers stand between a language model and five HTTP responses. The
/// model is asked for strict JSON and mostly complies, so the interesting cases
/// are all the ways "mostly" fails: a fenced block, prose around the array, a
/// number where a string was promised, a whole field missing.
///
/// The contract under test is that none of those throw. Before the split they
/// were unreachable without a live OpenAI call, and two of them did throw — a
/// numeric field reached <c>GetString()</c>, whose
/// <see cref="InvalidOperationException"/> slips past every
/// <c>catch (JsonException)</c> in the handler and becomes a 500 on a feature
/// that could have returned an empty list.
/// </summary>
public class BookDiscoveryResponsesTests
{
    private readonly IAiResponseParser _parser = new AiResponseParser();

    // ─── Author suggestions ──────────────────────────────────────────────

    [Fact]
    public void ReadsAuthorSuggestionsInOrder()
    {
        var authors = BookDiscoveryResponses.AuthorSuggestions(
            """[{"author":"J.R.R. Tolkien","confidence":"high"},{"author":"Christopher Tolkien","confidence":"medium"}]""",
            _parser);

        authors.Should().HaveCount(2);
        authors[0].Should().Be(new AnnasArchive.API.Models.AuthorSuggestion("J.R.R. Tolkien", "high"));
        authors[1].Confidence.Should().Be("medium");
    }

    [Fact]
    public void PullsTheArrayOutOfSurroundingProse()
    {
        // The prompt says no explanations; the model adds them anyway.
        var authors = BookDiscoveryResponses.AuthorSuggestions(
            """Sure! Here are the likely authors: [{"author":"Frank Herbert","confidence":"high"}] Hope that helps.""",
            _parser);

        authors.Should().ContainSingle().Which.Author.Should().Be("Frank Herbert");
    }

    [Fact]
    public void ReadsAuthorSuggestionsOutOfACodeFence()
    {
        var authors = BookDiscoveryResponses.AuthorSuggestions(
            "```json\n[{\"author\":\"Ursula K. Le Guin\",\"confidence\":\"high\"}]\n```",
            _parser);

        authors.Should().ContainSingle().Which.Author.Should().Be("Ursula K. Le Guin");
    }

    [Fact]
    public void DefaultsAConfidenceItCannotRead()
    {
        // A number where a string was promised. GetString() throws on this, and
        // the throw is not a JsonException, so it used to escape the handler.
        var authors = BookDiscoveryResponses.AuthorSuggestions(
            """[{"author":"Anonymous","confidence":3}]""", _parser);

        authors.Should().ContainSingle().Which.Confidence.Should().Be("low");
    }

    [Fact]
    public void SkipsAnAuthorEntryMissingEitherField()
    {
        var authors = BookDiscoveryResponses.AuthorSuggestions(
            """[{"author":"Complete","confidence":"high"},{"author":"No confidence"},{"confidence":"low"}]""",
            _parser);

        authors.Should().ContainSingle().Which.Author.Should().Be("Complete");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I don't recognise that title.")]
    [InlineData("{\"authors\": \"not an array\"}")]
    public void ReturnsNoAuthorsRatherThanThrowing(string? rawText)
    {
        BookDiscoveryResponses.AuthorSuggestions(rawText, _parser).Should().BeEmpty();
    }

    // ─── Related books ───────────────────────────────────────────────────

    [Fact]
    public void ReadsAFullRelatedBooksAnswer()
    {
        var payload = BookDiscoveryResponses.RelatedBooks(
            """
            {
              "seriesSummary": "A sprawling space opera.",
              "seriesName": "The Commonwealth Saga",
              "seriesSearchQuery": "commonwealth saga hamilton",
              "sameSeries": [
                {"title": "Pandora's Star", "order": 1, "description": "First contact goes badly."},
                {"title": "Judas Unchained", "order": 2, "description": "The war escalates."}
              ],
              "otherSeries": [
                {
                  "seriesName": "Night's Dawn",
                  "bookCount": 3,
                  "description": "Space horror.",
                  "summary": "The dead come back.",
                  "books": [{"title": "The Reality Dysfunction", "order": 1, "description": "It begins."}]
                }
              ]
            }
            """, _parser);

        payload.SeriesSummary.Should().Be("A sprawling space opera.");
        payload.SeriesName.Should().Be("The Commonwealth Saga");
        payload.SeriesSearchQuery.Should().Be("commonwealth saga hamilton");
        payload.SameSeries.Should().HaveCount(2);
        payload.SameSeries[1].Order.Should().Be(2);
        payload.OtherSeries.Should().ContainSingle();
        payload.OtherSeries[0].Books.Should().ContainSingle().Which.Title.Should().Be("The Reality Dysfunction");
    }

    [Fact]
    public void FallsBackToTheBookCountItCanSee()
    {
        // bookCount is the model's own claim and is often absent or wrong; the
        // list is the thing the frontend actually renders.
        var payload = BookDiscoveryResponses.RelatedBooks(
            """
            {"otherSeries":[{"seriesName":"Earthsea","books":[{"title":"A Wizard of Earthsea"},{"title":"The Tombs of Atuan"}]}]}
            """, _parser);

        payload.OtherSeries.Should().ContainSingle().Which.BookCount.Should().Be(2);
    }

    [Fact]
    public void TreatsANullSeriesSummaryAsNoSeries()
    {
        // The prompt explicitly asks for null when the book is a standalone.
        var payload = BookDiscoveryResponses.RelatedBooks(
            """{"seriesSummary": null, "sameSeries": []}""", _parser);

        payload.SeriesSummary.Should().BeNull();
        payload.SameSeries.Should().BeEmpty();
    }

    [Fact]
    public void SkipsASeriesBookWithNoTitle()
    {
        var payload = BookDiscoveryResponses.RelatedBooks(
            """{"sameSeries":[{"order":1,"description":"Nameless"},{"title":"Real Book","order":2}]}""",
            _parser);

        payload.SameSeries.Should().ContainSingle().Which.Title.Should().Be("Real Book");
    }

    [Fact]
    public void SurvivesAnOrderTheModelSpelledOut()
    {
        var payload = BookDiscoveryResponses.RelatedBooks(
            """{"sameSeries":[{"title":"Dune","order":"one"}]}""", _parser);

        payload.SameSeries.Should().ContainSingle().Which.Order.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void ReturnsAnEmptyRelatedBooksPayloadRatherThanThrowing(string? rawText)
    {
        var payload = BookDiscoveryResponses.RelatedBooks(rawText, _parser);

        payload.SameSeries.Should().BeEmpty();
        payload.OtherSeries.Should().BeEmpty();
        payload.SeriesSummary.Should().BeNull();
    }

    // ─── Book search ─────────────────────────────────────────────────────

    [Fact]
    public void ReadsABookSearchAnswer()
    {
        var payload = BookDiscoveryResponses.BookSearch(
            """
            {"isBookQuery": true, "summary": "Hugo winners of the 1960s.",
             "books": [{"title":"Dune","author":"Frank Herbert","summary":"Desert politics.","importance":"Best-selling SF novel ever."}]}
            """, _parser);

        payload.Should().NotBeNull();
        payload!.IsBookQuery.Should().BeTrue();
        payload.Summary.Should().Be("Hugo winners of the 1960s.");
        payload.Books.Should().ContainSingle();
        payload.Books[0].DescriptionSource.Should().Be("gpt");
        payload.Books[0].CoverUrl.Should().BeNull("covers are fetched lazily by the frontend");
    }

    [Fact]
    public void CarriesTheModelsRefusalMessage()
    {
        var payload = BookDiscoveryResponses.BookSearch(
            """{"isBookQuery": false, "message": "That's a recipe, not a book."}""", _parser);

        payload!.IsBookQuery.Should().BeFalse();
        payload.Message.Should().Be("That's a recipe, not a book.");
    }

    [Fact]
    public void DropsABookSearchResultWithNoTitle()
    {
        var payload = BookDiscoveryResponses.BookSearch(
            """{"isBookQuery":true,"books":[{"author":"Nobody","summary":"x"},{"title":"  ","author":"Blank"},{"title":"Real"}]}""",
            _parser);

        payload!.Books.Should().ContainSingle().Which.Title.Should().Be("Real");
    }

    [Fact]
    public void ReportsUnparseableBookSearchOutputAsNull()
    {
        // Null here is what makes the endpoint answer "try again or simplify
        // the query" rather than returning an empty list as if it had looked.
        BookDiscoveryResponses.BookSearch("Here's a list:\n1. Dune\n2. Neuromancer", _parser).Should().BeNull();
        BookDiscoveryResponses.BookSearch("[\"Dune\"]", _parser).Should().BeNull();
        BookDiscoveryResponses.BookSearch(null, _parser).Should().BeNull();
    }

    // ─── Series matching ─────────────────────────────────────────────────

    [Fact]
    public void ReadsSeriesMatches()
    {
        var matches = BookDiscoveryResponses.SeriesMatches(
            """
            {"matches":[
              {"bookTitle":"Pandora's Star","order":1,"status":"matched","selectedMd5":"abc123","selectedTitle":"Pandora's Star (Commonwealth Saga 1)","confidence":"exact","reason":"Exact title and author match"},
              {"bookTitle":"Judas Unchained","order":2,"status":"not_found","confidence":"uncertain","reason":"Only an omnibus was available"}
            ]}
            """, _parser);

        matches.Should().HaveCount(2);
        matches[0].SelectedMd5.Should().Be("abc123");
        matches[1].SelectedMd5.Should().BeNull("a not_found match names no file");
        matches[1].Status.Should().Be("not_found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"matches": "none"}""")]
    [InlineData("sorry, I couldn't match those")]
    public void ReturnsNoMatchesRatherThanThrowing(string? rawText)
    {
        BookDiscoveryResponses.SeriesMatches(rawText, _parser).Should().BeEmpty();
    }

    // ─── Result grouping ─────────────────────────────────────────────────
    //
    // Every index must come back exactly once. A book dropped here vanishes
    // from the user's search results entirely, which is worse than showing a
    // duplicate — so the invariant is asserted on every case below.

    [Fact]
    public void ReadsGroupsAsGiven()
    {
        var groups = BookDiscoveryResponses.GroupIndices("""{"groups":[[0,2],[1]]}""", 3, _parser);

        groups.Should().BeEquivalentTo(new[] { new[] { 0, 2 }, new[] { 1 } }, o => o.WithStrictOrdering());
        AssertCoversEveryIndex(groups, 3);
    }

    [Fact]
    public void GivesAnOmittedIndexItsOwnGroup()
    {
        var groups = BookDiscoveryResponses.GroupIndices("""{"groups":[[0,1]]}""", 4, _parser);

        groups.Should().HaveCount(3);
        AssertCoversEveryIndex(groups, 4);
    }

    [Fact]
    public void KeepsTheFirstClaimOnADuplicatedIndex()
    {
        var groups = BookDiscoveryResponses.GroupIndices("""{"groups":[[0,1],[1,2]]}""", 3, _parser);

        groups[0].Should().Equal(0, 1);
        groups[1].Should().Equal(2);
        AssertCoversEveryIndex(groups, 3);
    }

    [Fact]
    public void IgnoresAnIndexThatIsNotInTheRequest()
    {
        // A hallucinated index would otherwise throw on the caller's
        // request.Books[i] lookup.
        var groups = BookDiscoveryResponses.GroupIndices("""{"groups":[[0,99],[-1,1]]}""", 2, _parser);

        AssertCoversEveryIndex(groups, 2);
        groups.SelectMany(g => g).Should().OnlyContain(i => i >= 0 && i < 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("""{"groups":"all of them"}""")]
    [InlineData("""{"groups":[[]]}""")]
    public void FallsBackToNoGroupingWhenTheAnswerIsUnusable(string? rawText)
    {
        var groups = BookDiscoveryResponses.GroupIndices(rawText, 3, _parser);

        groups.Should().HaveCount(3, "every book still has to appear, ungrouped");
        AssertCoversEveryIndex(groups, 3);
    }

    [Fact]
    public void HandlesAnEmptyRequest()
    {
        BookDiscoveryResponses.GroupIndices("""{"groups":[]}""", 0, _parser).Should().BeEmpty();
    }

    private static void AssertCoversEveryIndex(List<List<int>> groups, int count) =>
        groups.SelectMany(g => g).Should().BeEquivalentTo(Enumerable.Range(0, count));
}
