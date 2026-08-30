namespace AnnasArchive.API.Services;

/// <summary>
/// The decisions in <see cref="CloudflareBypassService"/> that are pure functions
/// of their input.
///
/// <para>The service itself is a thin wrapper around a real Playwright browser and
/// is not honestly unit-testable — but these two are, and both matter: one decides
/// whether scraping traffic leaves through the VPN, the other decides whether a
/// Cloudflare challenge is solved once or once per spelling of the same domain.
/// Extracted rather than left inline for the same reason as
/// <see cref="AnnasArchive.API.Endpoints.AudiobookLibraryRules"/>.</para>
/// </summary>
public static class CloudflareBypassRules
{
    /// <summary>
    /// Whether a browser context routes through the VPN proxy.
    ///
    /// <para>Both halves are required: a proxy has to be configured, and the live
    /// toggle has to be on. This determines whether Anna's Archive traffic actually
    /// leaves through the VPN — the real scraping goes through the Playwright
    /// browser, not the plain HttpClient fallback — so an <c>||</c> here would leak
    /// traffic while the UI still showed the VPN as on. Evaluated per call, which is
    /// what makes flipping the toggle take effect on the very next request instead
    /// of needing a restart.</para>
    /// </summary>
    public static bool ShouldRouteThroughProxy(string? proxyUrl, bool vpnEnabled) =>
        !string.IsNullOrWhiteSpace(proxyUrl) && vpnEnabled;

    /// <summary>
    /// The cookie cache key for a domain. <c>https://www.example.org/</c> and
    /// <c>example.org</c> are the same site, so they must collapse to one entry —
    /// otherwise the same Cloudflare challenge is solved once per spelling, and
    /// repeated challenges are exactly the burst that makes Cloudflare harder on
    /// the next one.
    /// </summary>
    public static string NormalizeDomain(string domain)
    {
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri))
        {
            return uri.Host.Replace("www.", "");
        }
        return domain.Replace("www.", "").Replace("https://", "").Replace("http://", "").TrimEnd('/');
    }
}
