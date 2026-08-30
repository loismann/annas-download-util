using System.Net;
using AnnasArchive.Core.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The parser, run against a page LibGen actually served.
///
/// <para>Every other test in this area builds its own HTML, which proves the
/// parser handles the shape the test author had in mind and nothing more. That
/// is the wrong guarantee for a scraper: the failure this feature will actually
/// have is LibGen changing its markup, and a hand-written fixture cannot notice
/// that. The file beside this one is a real response — 84KB, 25 results,
/// captured 2026-08-20 — kept verbatim so a future change to it is visible in
/// the diff as a change to what the site sends.</para>
///
/// <para>It cannot tell you the live site still looks like this today. It can
/// tell you that whoever edits the selectors has not broken the shape that was
/// known to work, which is the part a unit test can honestly hold.</para>
/// </summary>
public class LibGenLiveMarkupTests
{
    private static string RealPage() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibGen", "general-search.html"));

    private static LibGenService ServiceReturning(string html)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });

        return new LibGenService(new HttpClient(handler.Object));
    }

    [Fact]
    public async Task The_parser_finds_results_in_a_page_libgen_really_served()
    {
        var results = (await ServiceReturning(RealPage()).SearchAsync("no country for old men", 25)).ToList();

        Assert.NotEmpty(results);
    }

    /// <summary>
    /// An md5 on every row, because the md5 is what the result is <i>for</i> —
    /// it is handed straight to Anna's download API. A row parsed without one is
    /// a title the reader can see and cannot obtain.
    /// </summary>
    [Fact]
    public async Task Every_parsed_result_carries_a_usable_md5()
    {
        var results = (await ServiceReturning(RealPage()).SearchAsync("no country for old men", 25)).ToList();

        Assert.All(results, book => Assert.Matches("^[0-9a-fA-F]{32}$", book.Md5));
    }

    [Fact]
    public async Task Titles_and_authors_survive_the_parse()
    {
        var results = (await ServiceReturning(RealPage()).SearchAsync("no country for old men", 25)).ToList();

        Assert.All(results, book => Assert.False(string.IsNullOrWhiteSpace(book.Title)));
        Assert.Contains(results, book => book.Authors.Count > 0);
    }
}
