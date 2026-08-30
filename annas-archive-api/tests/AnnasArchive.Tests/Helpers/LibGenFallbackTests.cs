using System.Net;
using System.Net.Http;
using System.Threading;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Moq.Protected;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// When a book Anna's does not have is fetched from LibGen instead.
///
/// <para>Search moved to LibGen when Anna's went behind DDoS-Guard; downloads
/// stayed on Anna's member API. That works only for files both catalogues hold,
/// and LibGen indexes books Anna's does not — for those the send buttons failed
/// outright. These pin the three things that make closing that gap safe: it
/// triggers on the right condition and only that one, it does not charge the
/// reader's Anna's allowance, and a LibGen failure reports Anna's answer rather
/// than replacing it with a scraping error.</para>
/// </summary>
public class LibGenFallbackTests
{
    private const string Md5 = "abc123def456789012345678901234ab";

    /// <summary>Answers one status to everything, so every mirror fails alike.</summary>
    private static HttpMessageHandler Answering(HttpStatusCode status, string body = "{}") =>
        Handler(_ => new HttpResponseMessage { StatusCode = status, Content = new StringContent(body) });

    private static HttpMessageHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => reply(req));
        return handler.Object;
    }

    private static AnnasArchiveDownloads Anna(HttpStatusCode status) =>
        new(new AnnasArchiveTransport(new HttpClient(Answering(status))));

    /// <summary>
    /// A LibGen that serves the book: the /main/{md5} page carries a GET link, and
    /// that link returns an EPUB.
    /// </summary>
    private static LibGenService LibGenThatHasTheBook() =>
        new(new HttpClient(Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/main/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(
                        "<html><body><a href='https://libgen.example/get/thebook.epub'>GET</a></body></html>")
                };

            var file = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
            file.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/epub+zip");
            return file;
        })))
        { };

    /// <summary>A LibGen that is reachable but has no download link on the page.</summary>
    private static LibGenService LibGenWithoutTheBook() =>
        new(new HttpClient(Handler(_ => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("<html><body>Nothing here</body></html>")
        })));

    private static Task<BookDownload> Attempt(HttpStatusCode annaStatus, LibGenService libgen) =>
        AnnaDownloadHelpers.DownloadBookAsync(Md5, "A Book", Anna(annaStatus), libgen, "test-key");

    /// <summary>The gap this closes: Anna's has no record, LibGen does.</summary>
    [Fact]
    public async Task ABookAnnasDoesNotHaveIsServedFromLibGen()
    {
        var download = await Attempt(HttpStatusCode.BadRequest, LibGenThatHasTheBook());

        download.Succeeded.Should().BeTrue();
        download.Source.Should().Be(BookSource.LibGen);
        download.FileName.Should().Be("A Book.epub");
    }

    /// <summary>
    /// The quota promise. LibGen has no membership and no daily allowance, so
    /// charging an Anna's slot would take a download away from the reader that
    /// Anna's never served.
    /// </summary>
    [Fact]
    public async Task ALibGenDownloadIsNotChargedToTheAnnasAllowance()
    {
        var download = await Attempt(HttpStatusCode.BadRequest, LibGenThatHasTheBook());

        download.CountsAgainstAnnasQuota.Should().BeFalse();
        download.AccountInfo.Should().BeNull(
            "there is no LibGen allowance to report, and Anna's counters would be a number "
            + "that has nothing to do with what just happened");
    }

    /// <summary>
    /// The fallback must not launder a spent allowance into a free download, and
    /// must not answer "unreachable" with a file — neither status says Anna's
    /// lacks the book.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AnnaDownloadFailure.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AnnaDownloadFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, AnnaDownloadFailure.Unavailable)]
    public async Task OnlyANotOnAnnasArchiveFailureFallsThrough(
        HttpStatusCode annaStatus, AnnaDownloadFailure expected)
    {
        // LibGen *would* serve it — so if the fallback fires, this succeeds and fails
        // the test. That is the point: the trigger, not the availability, is asserted.
        var download = await Attempt(annaStatus, LibGenThatHasTheBook());

        download.Succeeded.Should().BeFalse();
        download.Failure.Should().Be(expected);
        download.Source.Should().Be(BookSource.AnnasArchive);
    }

    /// <summary>
    /// Neither catalogue has it. The reader gets Anna's message, not a scraping
    /// error from the fallback — "not in either catalogue" is the useful fact.
    /// </summary>
    [Fact]
    public async Task WhenNeitherCatalogueHasItAnnasAnswerIsReported()
    {
        var download = await Attempt(HttpStatusCode.BadRequest, LibGenWithoutTheBook());

        download.Succeeded.Should().BeFalse();
        download.Failure.Should().Be(AnnaDownloadFailure.NotOnAnnasArchive);
        download.ErrorMessage.Should().Contain("not available from Anna's Archive");
        AnnaDownloadHelpers.StatusCodeFor(download.Failure).Should().Be(404);
    }

    /// <summary>
    /// A book Anna's does have never reaches LibGen — the fallback is a fallback,
    /// not a second opinion, and Anna's is the paid-for source.
    /// </summary>
    [Fact]
    public async Task AnAnnasSuccessNeverConsultsLibGen()
    {
        var libgenCalls = 0;
        var libgen = new LibGenService(new HttpClient(Handler(_ =>
        {
            libgenCalls++;
            return new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent("") };
        })));

        var annaJson = """
            {"download_url":"https://anna.example/get/book.epub",
             "account_fast_download_info":{"downloads_left":7,"downloads_per_day":10}}
            """;
        var anna = new AnnasArchiveDownloads(new AnnasArchiveTransport(
            new HttpClient(Handler(req =>
                req.RequestUri!.AbsolutePath.Contains("fast_download")
                    ? new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(annaJson) }
                    : new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new ByteArrayContent(new byte[] { 1 }) }))));

        var download = await AnnaDownloadHelpers.DownloadBookAsync(Md5, "A Book", anna, libgen, "test-key");

        download.Succeeded.Should().BeTrue();
        download.Source.Should().Be(BookSource.AnnasArchive);
        download.CountsAgainstAnnasQuota.Should().BeTrue();
        libgenCalls.Should().Be(0, "Anna's produced the file, so there was nothing to fall back for");
    }

    /// <summary>
    /// The fallback scrapes a page, so it can throw where an API call would not.
    /// A failure there must report Anna's problem, never replace it.
    /// </summary>
    [Fact]
    public async Task AThrowingLibGenDoesNotEscapeOrMaskAnnasAnswer()
    {
        var libgen = new LibGenService(new HttpClient(Handler(
            _ => throw new HttpRequestException("libgen is down"))));

        var attempt = async () => await Attempt(HttpStatusCode.BadRequest, libgen);

        var download = (await attempt.Should().NotThrowAsync()).Subject;
        download.Succeeded.Should().BeFalse();
        download.ErrorMessage.Should().Contain("not available from Anna's Archive");
    }
}
