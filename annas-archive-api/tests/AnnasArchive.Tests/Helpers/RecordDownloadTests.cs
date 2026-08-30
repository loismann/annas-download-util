using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// Whether a completed send is charged to the reader's Anna's allowance.
///
/// <para>The allowance is a real, small daily number. Charging a slot for a file
/// Anna's never served takes a download away from the reader — and not charging one
/// it did serve makes the badge on screen disagree with what Anna's will actually
/// allow. Both directions are wrong in a way nobody notices until they run out.</para>
///
/// <para>The rule itself is covered by <c>LibGenFallbackTests</c>, which pins what
/// each source reports. This is the other half: that the send routes act on it.</para>
/// </summary>
public class RecordDownloadTests
{
    private const string Md5 = "abc123def456789012345678901234ab";

    private static HttpContext ContextFor(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }

    private static Mock<IDownloadTrackingService> Tracking()
    {
        var tracking = new Mock<IDownloadTrackingService>();
        tracking.Setup(t => t.GetDownloadStatus()).Returns((7, 10));
        return tracking;
    }

    /// <summary>A file Anna's served is charged, because Anna's will charge it too.</summary>
    [Fact]
    public void A_download_Annas_served_is_charged_to_the_allowance()
    {
        var tracking = Tracking();

        SendToTargetHelpers.RecordDownload(
            ContextFor(new Claim(ClaimTypes.Email, "reader@test")),
            tracking.Object, Md5, "send-to-library", countsAgainstQuota: true);

        tracking.Verify(t => t.RecordDownload(Md5, "reader@test"), Times.Once);
    }

    /// <summary>
    /// The LibGen fallback is not. LibGen has no membership and no daily allowance,
    /// so charging one of Anna's slots for it takes a download away from the reader
    /// that Anna's never served.
    /// </summary>
    [Fact]
    public void A_download_the_fallback_served_is_not_charged()
    {
        var tracking = Tracking();

        SendToTargetHelpers.RecordDownload(
            ContextFor(new Claim(ClaimTypes.Email, "reader@test")),
            tracking.Object, Md5, "send-to-library", countsAgainstQuota: false);

        tracking.Verify(t => t.RecordDownload(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The counters come back either way. The badge has to stay truthful about what
    /// Anna's will allow next, and an uncharged download does not change that number —
    /// but the caller still has to report it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_counters_are_reported_whether_or_not_the_slot_was_charged(bool charged)
    {
        var counters = SendToTargetHelpers.RecordDownload(
            ContextFor(new Claim(ClaimTypes.Email, "reader@test")),
            Tracking().Object, Md5, "send-to-library", countsAgainstQuota: charged);

        counters.Should().NotBeNull();
    }

    /// <summary>
    /// Charged to an email when there is one, a name when there is not. The allowance
    /// is per person, so the identity picked here is what decides whose it comes out
    /// of — the same class of question as the AI allowance and the Kindle target.
    /// </summary>
    [Fact]
    public void A_download_is_charged_to_the_email_claim_when_one_is_present()
    {
        var tracking = Tracking();

        SendToTargetHelpers.RecordDownload(
            ContextFor(new Claim(ClaimTypes.Email, "reader@test"), new Claim(ClaimTypes.Name, "Reader")),
            tracking.Object, Md5, "send-to-library");

        tracking.Verify(t => t.RecordDownload(Md5, "reader@test"), Times.Once);
    }

    [Fact]
    public void A_download_falls_back_to_the_name_claim()
    {
        var tracking = Tracking();

        SendToTargetHelpers.RecordDownload(
            ContextFor(new Claim(ClaimTypes.Name, "Reader")),
            tracking.Object, Md5, "send-to-library");

        tracking.Verify(t => t.RecordDownload(Md5, "Reader"), Times.Once);
    }

    /// <summary>
    /// An unidentifiable caller is still charged, under "unknown". Fail-closed is the
    /// right direction here: the alternative is a request that consumed a real Anna's
    /// download and left no trace of it, which makes the counter drift from what
    /// Anna's itself believes.
    /// </summary>
    [Fact]
    public void An_unidentified_caller_is_still_charged_rather_than_going_unrecorded()
    {
        var tracking = Tracking();

        SendToTargetHelpers.RecordDownload(
            ContextFor(), tracking.Object, Md5, "send-to-library");

        tracking.Verify(t => t.RecordDownload(Md5, "unknown"), Times.Once);
    }
}
