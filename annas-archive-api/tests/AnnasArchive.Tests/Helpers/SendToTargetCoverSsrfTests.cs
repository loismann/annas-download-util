using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// Six endpoints take <c>coverUrl</c> off the query string and hand it to
/// <see cref="IEbookCoverService.ReplaceCoverAsync"/>, which fetches it from inside
/// the compose network. The only check was that it parsed as an absolute URI, so an
/// authenticated household member could point the server at Radarr, Seq, or the
/// cloud metadata endpoint and have it fetch from a trusted position.
/// </summary>
public class SendToTargetCoverSsrfTests
{
    [Theory]
    [InlineData("http://127.0.0.1/cover.jpg")]           // loopback
    [InlineData("http://10.0.0.5/cover.jpg")]            // private
    [InlineData("http://172.18.0.4:7878/cover.jpg")]     // the docker compose network
    [InlineData("http://192.168.1.10/cover.jpg")]        // home LAN
    [InlineData("http://169.254.169.254/cover.jpg")]     // link-local / cloud metadata
    [InlineData("http://100.101.102.103/cover.jpg")]     // CGNAT (Tailscale)
    [InlineData("http://[::1]/cover.jpg")]               // IPv6 loopback
    [InlineData("http://[::ffff:10.0.0.5]/cover.jpg")]   // IPv4 private wearing a v6 costume
    public async Task NeverFetchesACoverFromAnAddressInsideOurOwnNetwork(string coverUrl)
    {
        var cover = new RecordingCoverService();

        var result = await SendToTargetHelpers.TryReplaceCoverAsync(
            Stream.Null, coverUrl, "book.epub", cover, "test");

        cover.ReplaceWasCalled.Should().BeFalse();
        result.Should().BeSameAs(Stream.Null, "the original ebook is returned untouched");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/cover.jpg")]
    [InlineData("gopher://example.com/cover.jpg")]
    public async Task RejectsSchemesThatAreNotHttp(string coverUrl)
    {
        var cover = new RecordingCoverService();

        await SendToTargetHelpers.TryReplaceCoverAsync(
            Stream.Null, coverUrl, "book.epub", cover, "test");

        cover.ReplaceWasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task StillSkipsQuietlyWhenNoCoverWasAskedFor()
    {
        var cover = new RecordingCoverService();

        await SendToTargetHelpers.TryReplaceCoverAsync(
            Stream.Null, null, "book.epub", cover, "test");

        cover.ReplaceWasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task StillRejectsBeforeCheckingWhetherTheFormatIsSupported()
    {
        // Order matters only for what gets logged, but a private URL must not reach
        // the service even for a format it would have accepted.
        var cover = new RecordingCoverService();

        await SendToTargetHelpers.TryReplaceCoverAsync(
            Stream.Null, "http://10.0.0.5/cover.jpg", "book.pdf", cover, "test");

        cover.ReplaceWasCalled.Should().BeFalse();
    }

    private sealed class RecordingCoverService : IEbookCoverService
    {
        public bool ReplaceWasCalled { get; private set; }

        public bool IsFormatSupported(string format) => true;

        public Task<Stream> ReplaceCoverAsync(Stream ebookStream, string coverUrl, string format)
        {
            ReplaceWasCalled = true;
            return Task.FromResult(ebookStream);
        }
    }
}
