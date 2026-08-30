using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;
using Moq.Protected;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// What deleting an audiobook actually asks Audiobookshelf to do.
///
/// <para>The one detail that decides whether the delete is real is a query
/// parameter. Without <c>hard=1</c>, Audiobookshelf only unlinks the item from its
/// database and leaves the audio on disk — so the next library scan re-imports it
/// and the deletion silently undoes itself, hours later, with no error anywhere.
/// The endpoint would have returned 204 and the caller would have watched the book
/// come back.</para>
///
/// <para>Asserted against the request sent rather than a stub's return value,
/// because the request <i>is</i> the behaviour here.</para>
/// </summary>
public class AudiobookshelfDeleteTests
{
    private sealed class Conversation
    {
        public List<(HttpMethod Method, string Url)> Sent { get; } = [];

        public HttpMessageHandler Handler(HttpStatusCode status)
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                {
                    // AbsoluteUri, not ToString(): ToString() un-escapes what it can,
                    // so a %20 comes back as a space and an escaping assertion made
                    // against it fails even though the request on the wire was correct.
                    Sent.Add((req.Method, req.RequestUri!.AbsoluteUri));
                    return new HttpResponseMessage(status)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                });
            return mock.Object;
        }
    }

    private static (AudiobookshelfService Svc, Conversation Rec) Service(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var rec = new Conversation();
        var http = new HttpClient(rec.Handler(status));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audiobookshelf:BaseUrl"] = "http://abs.test",
                ["Audiobookshelf:ApiKey"] = "k"
            })
            .Build();
        return (new AudiobookshelfService(http, config), rec);
    }

    /// <summary>
    /// The flag that makes the delete real. A soft delete leaves the files, the next
    /// scan re-imports them, and the book reappears — which reads as the app losing
    /// the deletion rather than as Audiobookshelf doing what it was asked.
    /// </summary>
    [Fact]
    public async Task DeletingAnAudiobookAsksForAHardDeleteSoTheFilesActuallyGo()
    {
        var (svc, rec) = Service();

        await svc.DeleteItemAsync("li_abc123");

        rec.Sent.Should().ContainSingle();
        rec.Sent[0].Method.Should().Be(HttpMethod.Delete);
        rec.Sent[0].Url.Should().Contain("/api/items/li_abc123");
        rec.Sent[0].Url.Should().Contain("hard=1",
            "without it the audio stays on disk and the next scan brings the book back");
    }

    /// <summary>
    /// The id goes in the path, so it is escaped. Audiobookshelf ids are opaque
    /// strings from another system: one containing a slash would otherwise change
    /// which resource the request names rather than being rejected.
    /// </summary>
    [Theory]
    [InlineData("li_with space", "li_with%20space")]
    [InlineData("li_a/b", "li_a%2Fb")]
    [InlineData("li_a?b", "li_a%3Fb")]
    [InlineData("li_a#b", "li_a%23b")]
    public async Task AnItemIdIsEscapedRatherThanPastedIntoThePath(string id, string expected)
    {
        var (svc, rec) = Service();

        await svc.DeleteItemAsync(id);

        rec.Sent[0].Url.Should().Contain($"/api/items/{expected}");
    }

    /// <summary>
    /// A refused delete has to raise. The endpoint turns it into a 502 and — this is
    /// the part that matters — <b>skips the local cleanup</b>, so the app keeps the
    /// owners, genres and favourites for an item that still exists. Swallowing the
    /// failure would drop all of that for a book still sitting in the library.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ADeleteAudiobookshelfRefusesIsRaisedRatherThanSwallowed(HttpStatusCode status)
    {
        var (svc, _) = Service(status);

        var delete = async () => await svc.DeleteItemAsync("li_abc123");

        await delete.Should().ThrowAsync<HttpRequestException>();
    }
}
