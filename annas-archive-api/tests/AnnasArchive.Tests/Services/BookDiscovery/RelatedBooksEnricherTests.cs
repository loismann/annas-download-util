using System.Net.Http;
using System.Threading;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.API.Services.BookDiscovery;
using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq.Protected;

namespace AnnasArchive.Tests.Services.BookDiscovery;

/// <summary>
/// The description budget is the reason this needed extracting. Eight lookups
/// are allowed per request, shared between the current series and the author's
/// other series — one number protecting one rate limit. Two independent budgets
/// would let a single request spend sixteen, which is exactly the mistake the
/// old nested loops were one edit away from.
/// </summary>
public class RelatedBooksEnricherTests
{
    /// <summary>Matches AiThrottlingConfiguration.MaxRelatedBookDescriptions.
    /// Pinned here so a change to that number fails a test that says why it
    /// matters rather than silently doubling the request's API spend.</summary>
    private const int Budget = 8;

    private const string BillTo = "acct-paul";

    private readonly Mock<IWikipediaService> _wikipedia = new();
    private readonly Mock<IAiChatCompletion> _chat = new();

    public RelatedBooksEnricherTests()
    {
        _wikipedia
            .Setup(w => w.GetBookDescriptionAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync("A description long enough to count.");

        _chat
            .Setup(c => c.CompleteAsync(It.IsAny<AiChatCall>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiChatOutcome("A model-written description.", null));
    }

    // ─── Description budget ──────────────────────────────────────────────

    [Fact]
    public async Task FillsTheDescriptionsThatAreMissing()
    {
        var (same, _) = await Enricher().FillDescriptionsAsync(
            [Book("Pandora's Star"), Book("Judas Unchained")], [], "Peter F. Hamilton", "gpt-4o", BillTo);

        same.Should().OnlyContain(b => b.DescriptionSource == "wikipedia");
        same.Should().OnlyContain(b => b.Description == "A description long enough to count.");
    }

    [Fact]
    public async Task LeavesABookThatAlreadyHasADescription()
    {
        var described = Book("Already Described") with { Description = "A real description already." };

        var (same, _) = await Enricher().FillDescriptionsAsync([described], [], "Author", "gpt-4o", BillTo);

        same.Should().ContainSingle().Which.Should().Be(described);
        _wikipedia.Verify(w => w.GetBookDescriptionAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TreatsAStubDescriptionAsMissing()
    {
        // Models emit "TBD" or "N/A" to satisfy the schema. Under ten
        // characters is the line between a placeholder and an answer.
        var stub = Book("Stubbed") with { Description = "TBD" };

        var (same, _) = await Enricher().FillDescriptionsAsync([stub], [], "Author", "gpt-4o", BillTo);

        same.Should().ContainSingle().Which.DescriptionSource.Should().Be("wikipedia");
    }

    [Fact]
    public async Task StopsAtTheBudgetWithinOneSeries()
    {
        var books = Enumerable.Range(1, Budget + 4).Select(i => Book($"Book {i}")).ToList();

        var (same, _) = await Enricher().FillDescriptionsAsync(books, [], "Author", "gpt-4o", BillTo);

        same.Should().HaveCount(Budget + 4, "every book is still returned, described or not");
        same.Count(b => b.DescriptionSource is not null).Should().Be(Budget);
        WikipediaCalls().Should().Be(Budget);
    }

    [Fact]
    public async Task OtherSeriesSpendsWhatIsLeftRatherThanItsOwnBudget()
    {
        var same = Enumerable.Range(1, 5).Select(i => Book($"Same {i}")).ToList();
        var other = new List<AuthorSeries>
        {
            Series("Other One", 4),
            Series("Other Two", 4)
        };

        var (filledSame, filledOther) = await Enricher().FillDescriptionsAsync(same, other, "Author", "gpt-4o", BillTo);

        filledSame.Count(b => b.DescriptionSource is not null).Should().Be(5);
        filledOther.SelectMany(s => s.Books).Count(b => b.DescriptionSource is not null).Should()
            .Be(Budget - 5, "the two lists share one budget");
        WikipediaCalls().Should().Be(Budget);
    }

    [Fact]
    public async Task LeavesOtherSeriesUntouchedWhenTheBudgetIsAlreadySpent()
    {
        var same = Enumerable.Range(1, Budget).Select(i => Book($"Same {i}")).ToList();
        var other = new List<AuthorSeries> { Series("Untouched", 3) };

        var (_, filledOther) = await Enricher().FillDescriptionsAsync(same, other, "Author", "gpt-4o", BillTo);

        filledOther.Should().BeEquivalentTo(other, "nothing was left to spend on them");
    }

    [Fact]
    public async Task FallsBackToTheModelWhenWikipediaHasNothing()
    {
        _wikipedia
            .Setup(w => w.GetBookDescriptionAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string?)null);

        var (same, _) = await Enricher().FillDescriptionsAsync([Book("Obscure")], [], "Author", "gpt-4o", BillTo);

        same.Should().ContainSingle().Which.DescriptionSource.Should().Be("gpt");
    }

    [Fact]
    public async Task ChargesTheRequesterForEveryDescriptionItGenerates()
    {
        // This pass makes up to eight model calls on top of the one the endpoint
        // already billed for. All eight used to be invisible to the monthly
        // allowance that is supposed to cap them.
        _wikipedia
            .Setup(w => w.GetBookDescriptionAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string?)null);

        var books = Enumerable.Range(1, 3).Select(i => Book($"Book {i}")).ToList();

        await Enricher().FillDescriptionsAsync(books, [], "Author", "gpt-4o", BillTo);

        _chat.Verify(
            c => c.CompleteAsync(It.IsAny<AiChatCall>(), BillTo, It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task KeepsEveryOtherFieldOfABookItDescribes()
    {
        var (same, _) = await Enricher().FillDescriptionsAsync(
            [new SeriesBook("Titled", 7, "", "http://cover")], [], "Author", "gpt-4o", BillTo);

        var book = same.Should().ContainSingle().Subject;
        book.Title.Should().Be("Titled");
        book.Order.Should().Be(7);
        book.CoverUrl.Should().Be("http://cover");
    }

    [Fact]
    public async Task DoesNothingWithNothingToDo()
    {
        var (same, other) = await Enricher().FillDescriptionsAsync([], [], "Author", "gpt-4o", BillTo);

        same.Should().BeEmpty();
        other.Should().BeEmpty();
        WikipediaCalls().Should().Be(0);
    }

    // ─── Series expansion ────────────────────────────────────────────────

    [Fact]
    public async Task DoesNotSearchTheCatalogueForAnAlreadyLongSeries()
    {
        var handler = Handler("<html></html>");
        var books = Enumerable.Range(1, RelatedBooksEnricher.ExpansionThreshold)
            .Select(i => Book($"Book {i}")).ToList();

        var result = await Enricher(handler).ExpandSameSeriesAsync(books, Request(), Payload());

        result.Should().BeSameAs(books);
        handler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task KeepsTheModelsListWhenTheCatalogueFindsNothingBetter()
    {
        // A search that matched fewer titles than the model listed is evidence
        // about the catalogue, not about the series.
        var books = new List<SeriesBook> { Book("One"), Book("Two") };

        var result = await Enricher(Handler("<html><body>no results</body></html>"))
            .ExpandSameSeriesAsync(books, Request(), Payload());

        result.Should().BeEquivalentTo(books);
    }

    [Fact]
    public async Task KeepsTheModelsListWhenTheCatalogueIsUnreachable()
    {
        // A failed search costs a longer list, not the answer.
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Cloudflare said no"));

        var books = new List<SeriesBook> { Book("One") };

        var result = await Enricher(handler).ExpandSameSeriesAsync(books, Request(), Payload());

        result.Should().BeEquivalentTo(books);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static SeriesBook Book(string title) => new(title, 0, "", null);

    private static AuthorSeries Series(string name, int bookCount) =>
        new(name, bookCount, Enumerable.Range(1, bookCount).Select(i => Book($"{name} {i}")).ToList(), "", "");

    private static RelatedBooksRequest Request() => new("Pandora's Star", "Peter F. Hamilton");

    private static RelatedBooksPayload Payload() => RelatedBooksPayload.Empty;

    private int WikipediaCalls() =>
        _wikipedia.Invocations.Count(i => i.Method.Name == nameof(IWikipediaService.GetBookDescriptionAsync));

    private static Mock<HttpMessageHandler> Handler(string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(body)
            });
        return handler;
    }

    private RelatedBooksEnricher Enricher(Mock<HttpMessageHandler>? searchHandler = null)
    {
        var handler = searchHandler ?? Handler("<html></html>");
        var searchClient = new HttpClient(handler.Object) { BaseAddress = new Uri("https://annas-archive.org") };
        var annaArchive = new AnnasArchiveService(searchClient, new MemoryCache(new MemoryCacheOptions()));

        return new RelatedBooksEnricher(annaArchive, _wikipedia.Object, _chat.Object);
    }
}
