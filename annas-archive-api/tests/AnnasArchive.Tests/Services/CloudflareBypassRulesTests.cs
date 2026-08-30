using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The two decisions in <see cref="CloudflareBypassService"/> that are pure
/// functions of their input.
///
/// <para>348 lines with no test naming it, but almost all of it is a thin wrapper
/// around a real Playwright browser — there is no honest way to unit-test launching
/// Chromium, and pretending otherwise would produce tests that assert a mock.
/// These two are different: one decides whether scraping traffic leaves through the
/// VPN, and the other decides whether a Cloudflare challenge gets solved once or
/// once per spelling of the same domain.</para>
/// </summary>
public class CloudflareBypassRulesTests
{
    // ------------------------------------------------------------ the proxy

    /// <summary>
    /// Both halves are required. This is the decision that determines whether
    /// Anna's Archive traffic actually leaves through the VPN — the real scraping
    /// goes through this browser, not the plain HttpClient fallback — so an
    /// "or" here would leak traffic while the UI still showed the VPN as on.
    /// </summary>
    [Theory]
    [InlineData("http://gluetun:8888", true, true)]    // configured and switched on
    [InlineData("http://gluetun:8888", false, false)]  // switched off — direct
    [InlineData(null, true, false)]                    // nothing to route through
    [InlineData("", true, false)]
    [InlineData("   ", true, false)]
    [InlineData(null, false, false)]
    public void TrafficRoutesThroughTheProxyOnlyWhenOneIsConfiguredAndTheToggleIsOn(
        string? proxyUrl, bool vpnEnabled, bool expected)
    {
        CloudflareBypassRules.ShouldRouteThroughProxy(proxyUrl, vpnEnabled).Should().Be(expected);
    }

    // ----------------------------------------------------------- the domain

    /// <summary>
    /// Cookies are cached per host, so every spelling of the same site has to
    /// collapse to one key. A miss here means solving the same Cloudflare challenge
    /// again — which is slow, and is exactly the burst of requests that makes
    /// Cloudflare harder on the next one.
    /// </summary>
    [Theory]
    [InlineData("example.org", "example.org")]
    [InlineData("www.example.org", "example.org")]
    [InlineData("https://example.org", "example.org")]
    [InlineData("https://www.example.org", "example.org")]
    [InlineData("http://example.org/", "example.org")]
    [InlineData("https://example.org/search?q=x", "example.org")]
    [InlineData("example.org/", "example.org")]
    public void EverySpellingOfADomainCollapsesToOneCacheKey(string input, string expected)
    {
        CloudflareBypassRules.NormalizeDomain(input).Should().Be(expected);
    }

    /// <summary>A port is part of the identity of a host and must not be discarded.</summary>
    [Fact]
    public void AHostIsNotConfusedWithADifferentOneOnTheSameName()
    {
        CloudflareBypassRules.NormalizeDomain("https://annas-archive.org")
            .Should().NotBe(CloudflareBypassRules.NormalizeDomain("https://libgen.rs"));
    }
}
