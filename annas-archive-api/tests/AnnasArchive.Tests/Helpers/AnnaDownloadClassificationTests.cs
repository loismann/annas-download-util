using System.Net;
using System.Net.Http;
using System.Threading;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Moq.Protected;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// Why Anna's Archive refused, asserted from the wire inwards.
///
/// <para><b>What this exists to catch.</b> <c>AnnaDownloadFailureTests</c> tests
/// <c>StatusCodeFor</c>, which is a lookup table — it proves 429 maps to 429 and
/// says nothing about whether a real rate limit ever reaches
/// <c>RateLimited</c>. It did not. The transport rethrew
/// <c>new HttpRequestException($"Request failed with status {status}")</c>, whose
/// <c>StatusCode</c> is null, so both <c>when (ex.StatusCode == TooManyRequests)</c>
/// filters on the download path never matched. The rate-limit handling was dead
/// code, and a 400, a 429 and a 503 all escaped as the same unhandled 500 — which
/// is how Anna's answering 400 for an md5 it has not indexed showed up as "the
/// send button is broken" instead of "this book is not on Anna's".</para>
///
/// <para>So these drive a real <see cref="AnnasArchiveTransport"/> over a stubbed
/// handler and assert the classification, not the mapping. Five tests passed over
/// this defect because each checked itself against its own assumption.</para>
/// </summary>
public class AnnaDownloadClassificationTests
{
    private const string Md5 = "abc123def456789012345678901234ab";

    /// <summary>
    /// Answers <paramref name="status"/> to every request, so all three mirrors
    /// fail the same way and the transport reaches its give-up path.
    /// </summary>
    private static AnnasArchiveDownloads DownloadsAnswering(HttpStatusCode status, string body = "{}")
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(body)
            });

        return new AnnasArchiveDownloads(
            new AnnasArchiveTransport(new HttpClient(handler.Object)));
    }

    private static Task<(HttpResponseMessage? response, string? fileName,
        AccountFastDownloadInfoDto? accountInfo, string? errorMessage, AnnaDownloadFailure failure)>
        Attempt(HttpStatusCode status) =>
            AnnaDownloadHelpers.DownloadBookFromAnnasArchiveAsync(
                Md5, "A Book", DownloadsAnswering(status), "test-key");

    /// <summary>
    /// The regression that started this: 400 is Anna's saying it has no record of
    /// the md5, which is routine now that search comes from LibGen.
    /// </summary>
    [Fact]
    public async Task ABadRequestIsReportedAsNotOnAnnasArchive()
    {
        var (_, _, _, message, failure) = await Attempt(HttpStatusCode.BadRequest);

        failure.Should().Be(AnnaDownloadFailure.NotOnAnnasArchive);
        AnnaDownloadHelpers.StatusCodeFor(failure).Should().Be(404);
        message.Should().Contain("not available from Anna's Archive");
    }

    [Fact]
    public async Task ANotFoundIsReportedAsNotOnAnnasArchive()
    {
        var (_, _, _, _, failure) = await Attempt(HttpStatusCode.NotFound);

        failure.Should().Be(AnnaDownloadFailure.NotOnAnnasArchive);
    }

    /// <summary>
    /// The case whose handling existed but could never run. If the transport ever
    /// stops carrying the status again, this is what fails.
    /// </summary>
    [Fact]
    public async Task ARateLimitReachesRateLimitedRatherThanEscaping()
    {
        var (_, _, _, message, failure) = await Attempt(HttpStatusCode.TooManyRequests);

        failure.Should().Be(AnnaDownloadFailure.RateLimited);
        AnnaDownloadHelpers.StatusCodeFor(failure).Should().Be(429);
        message.Should().Contain("Rate limit");
    }

    /// <summary>
    /// A dead mirror is not the same answer as a book Anna's never had, and the
    /// two must not collapse — a LibGen fallback keys off the difference.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task AnUnreachableMirrorIsUnavailableNotNotOnAnnasArchive(HttpStatusCode status)
    {
        var (_, _, _, _, failure) = await Attempt(status);

        failure.Should().Be(AnnaDownloadFailure.Unavailable);
        AnnaDownloadHelpers.StatusCodeFor(failure).Should().Be(502);
    }

    /// <summary>
    /// No status escapes as an exception any more. This is the property that was
    /// actually violated in production, and it is asserted over the whole range
    /// rather than the three codes that happened to be seen.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task NoUpstreamStatusEscapesAsAnUnhandledException(HttpStatusCode status)
    {
        var attempt = async () => await Attempt(status);

        await attempt.Should().NotThrowAsync(
            "an unhandled exception here is a 500 with no explanation, which is what "
            + "made a book Anna's does not have look like a broken button");
    }

    /// <summary>
    /// Every failure carries something a person can read. An empty message is how
    /// a 500 with no explanation gets reintroduced without failing a status check.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EveryFailureCarriesAMessageAndNoFile(HttpStatusCode status)
    {
        var (response, fileName, _, message, failure) = await Attempt(status);

        failure.Should().NotBe(AnnaDownloadFailure.None);
        message.Should().NotBeNullOrWhiteSpace();
        response.Should().BeNull();
        fileName.Should().BeNull();
    }

    /// <summary>
    /// The transport is the single point the classification depends on, so it is
    /// asserted directly too: everything above is downstream of this one property.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TheTransportCarriesTheUpstreamStatusOnTheException(HttpStatusCode status)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage { StatusCode = status, Content = new StringContent("{}") });

        var transport = new AnnasArchiveTransport(new HttpClient(handler.Object));

        var act = async () => await transport.GetJsonElementAsync("/dyn/api/fast_download.json?md5=" + Md5);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(status,
                "a null StatusCode here silently disables every `when (ex.StatusCode == ...)` "
                + "filter downstream, which is the defect this whole file exists for");
    }
}
