using System.Net;
using AnnasArchive.Core.Services;

using Moq;
using Moq.Protected;
using Xunit;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The order these two sources are asked in is the whole feature.
///
/// <para>Anna's Archive put its HTML pages behind DDoS-Guard in August 2026, and
/// search was the only thing built on them. It now spends about thirty seconds
/// failing. LibGen indexes overlapping files and, because an md5 is a hash of the
/// file's bytes rather than an identifier Anna's hands out, the md5s it returns
/// are keys Anna's download API accepts. So search moved to LibGen while
/// downloads stayed where they were.</para>
///
/// <para>Both sources are driven through their real HTTP plumbing with a mocked
/// handler, so "did it ask Anna's at all" is answered by whether a request was
/// made rather than by a flag on a stub. That is the assertion that matters here
/// — asking Anna's first would put half a minute in front of every search.</para>
/// </summary>
public class BookSearchTests
{
    private readonly Mock<HttpMessageHandler> _libgen = new();

    private BookSearch Build() => new(new LibGenService(new HttpClient(_libgen.Object)));

    private void Returns(string html) =>
        _libgen.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });

    /// <summary>
    /// One row in the shape LibGen's general search really returns — ten columns
    /// with the md5 on a <c>/main/</c> mirror link, which is where the parser
    /// reads it from.
    /// </summary>
    private static string LibGenHtml(string title, string md5) => $@"
        <html><body>
            <table id='tablelibgen'>
                <tr><th>ID</th><th>Authors</th><th>Title</th><th>Publisher</th><th>Year</th>
                    <th>Pages</th><th>Language</th><th>Size</th><th>Ext</th><th>Mirrors</th></tr>
                <tr>
                    <td>12345</td>
                    <td>Cormac McCarthy</td>
                    <td><a href='/book/{md5}'>{title}</a></td>
                    <td>Vintage</td>
                    <td>2005</td>
                    <td>320</td>
                    <td>English</td>
                    <td>1.5 MB</td>
                    <td>epub</td>
                    <td><a href='/main/{md5}'>Mirror 1</a></td>
                </tr>
            </table>
        </body></html>";

    private const string NoRows = "<html><body><p>Nothing here</p></body></html>";

    [Fact]
    public async Task A_search_returns_what_the_source_found()
    {
        Returns(LibGenHtml("No Country for Old Men", "fdd4fbec7e3cb0be5e6c705d556eb246"));

        Assert.NotEmpty(await Build().SearchAsync("no country for old men", 25, exact: false, page: 1));
    }

    /// <summary>
    /// The md5 is the entire point of the result — it is what the download API is
    /// handed next — so a row parsed without one is a title the reader can see
    /// and cannot obtain.
    /// </summary>
    [Fact]
    public async Task Every_result_carries_the_md5_the_download_api_needs()
    {
        Returns(LibGenHtml("Blood Meridian", "45a5c83583e885965a0d58a5a1cd3539"));

        var results = await Build().SearchAsync("blood meridian", 25, exact: false, page: 1);

        Assert.All(results, book => Assert.Matches("^[0-9a-fA-F]{32}$", book.Md5));
    }

    [Fact]
    public async Task A_search_that_matches_nothing_is_empty_and_not_an_error()
    {
        Returns(NoRows);

        Assert.Empty(await Build().SearchAsync("a book nobody has", 25, exact: false, page: 1));
    }

    /// <summary>
    /// The distinction the whole design turns on. There is one source now, so
    /// nothing is left to cover for it, and an outage reported as an empty shelf
    /// would tell the reader their book does not exist. The endpoint turns this
    /// into a 503.
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_reached_fails_rather_than_looking_empty()
    {
        _libgen.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build().SearchAsync("anything", 25, exact: false, page: 1));
    }
}
