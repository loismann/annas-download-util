using System.Net;
using AnnasArchive.API.Configuration;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Configuration;

public class GoogleBooksApiKeyHandlerTests
{
    [Fact]
    public async Task AppendsTheKeyToAQueryThatAlreadyHasParameters()
    {
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes?q=dune", key: "SECRET");

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?q=dune&key=SECRET");
    }

    [Fact]
    public async Task AppendsTheKeyToAUrlWithNoQueryAtAll()
    {
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes", key: "SECRET");

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?key=SECRET");
    }

    [Fact]
    public async Task DoesNotAddASecondKeyWhenTheCallerAlreadySentOne()
    {
        // AudiobookEnrichmentService builds its own `&key=`; two would be sent
        // otherwise, and Google rejects the request rather than picking one.
        var uri = await SendAsync(
            "https://www.googleapis.com/books/v1/volumes?q=dune&key=CALLER", key: "SECRET");

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?q=dune&key=CALLER");
    }

    [Fact]
    public async Task DoesNotAddASecondKeyWhenKeyIsTheFirstParameter()
    {
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes?key=CALLER", key: "SECRET");

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?key=CALLER");
    }

    [Fact]
    public async Task LeavesTheRequestAloneWhenAQueryMerelyContainsTheWordKey()
    {
        // Matching on a bare "key=" substring would rewrite this one.
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes?q=monkey%3Dbusiness", key: "SECRET");

        uri.Should().EndWith("&key=SECRET");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsANoOpWhenNoKeyIsConfigured(string? key)
    {
        // Anonymous requests are a supported state — lower quota, not an error.
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes?q=dune", key);

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?q=dune");
    }

    [Fact]
    public async Task EscapesAKeyContainingUrlSignificantCharacters()
    {
        var uri = await SendAsync("https://www.googleapis.com/books/v1/volumes?q=dune", key: "a b&c");

        uri.Should().Be("https://www.googleapis.com/books/v1/volumes?q=dune&key=a%20b%26c");
    }

    private static async Task<string> SendAsync(string requestUri, string? key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleBooks:ApiKey"] = key })
            .Build();

        var capture = new CapturingHandler();
        var handler = new GoogleBooksApiKeyHandler(config) { InnerHandler = capture };

        using var invoker = new HttpMessageInvoker(handler);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri), CancellationToken.None);

        // AbsoluteUri, not ToString(): ToString() un-escapes for display, so an
        // escaped key reads back as though it were never escaped. AbsoluteUri is
        // what HttpClient actually puts on the wire.
        return capture.SeenUri!.AbsoluteUri;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? SeenUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
