using AnnasArchive.API.Helpers;
using Microsoft.AspNetCore.Http;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// How a failed Anna's Archive download is reported.
///
/// All three download endpoints share this mapping so they cannot disagree about
/// what a rate limit looks like from the outside — they previously each answered
/// HTTP 200 with <c>success = false</c>, which made every download failure
/// indistinguishable from a success to anything reading status codes.
/// </summary>
public class AnnaDownloadFailureTests
{
    /// <summary>
    /// A rate limit is the caller's own quota and clears on its own; 429 says
    /// "try again shortly", which is both true and actionable.
    /// </summary>
    [Fact]
    public void RateLimited_IsReportedAs429()
    {
        AnnaDownloadHelpers.StatusCodeFor(AnnaDownloadFailure.RateLimited)
            .Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// No download URL, or a refused transfer, is Anna's Archive failing us — an
    /// upstream problem, not something the caller did.
    /// </summary>
    [Fact]
    public void Unavailable_IsReportedAs502()
    {
        AnnaDownloadHelpers.StatusCodeFor(AnnaDownloadFailure.Unavailable)
            .Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public void None_IsReportedAs200()
    {
        AnnaDownloadHelpers.StatusCodeFor(AnnaDownloadFailure.None)
            .Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// The distinction is the whole point of the enum: collapsing both failures
    /// onto one status would put "wait a minute" and "the mirror is down" in the
    /// same bucket, which is what <c>success = false</c> did.
    /// </summary>
    [Fact]
    public void TheTwoFailureKindsDoNotCollapseOntoOneStatus()
    {
        AnnaDownloadHelpers.StatusCodeFor(AnnaDownloadFailure.RateLimited)
            .Should().NotBe(AnnaDownloadHelpers.StatusCodeFor(AnnaDownloadFailure.Unavailable));
    }

    /// <summary>Every failure reports as an error, never as a success.</summary>
    [Theory]
    [InlineData(AnnaDownloadFailure.RateLimited)]
    [InlineData(AnnaDownloadFailure.Unavailable)]
    public void EveryFailureIsANonSuccessStatus(AnnaDownloadFailure failure)
    {
        AnnaDownloadHelpers.StatusCodeFor(failure).Should().BeGreaterThanOrEqualTo(400);
    }
}
