#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using AnnasArchive.Core.Telemetry;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Every request to Anna's Archive goes through here. The site is behind
/// Cloudflare and its domains go down individually and without warning, so a
/// single fetch is really "try Playwright against each mirror, then plain
/// HttpClient against each mirror, and give up only when all of them fail".
///
/// Split out of <see cref="AnnasArchiveService"/>: search, covers and
/// downloads all need that behaviour, and none of them should have to know
/// about it. This is the only place that knows the mirror list or the order
/// the two fetch strategies are tried in.
/// </summary>
public class AnnasArchiveTransport
{
    private readonly HttpClient _http;
    private readonly Func<string, Task<string>>? _playwrightFetcher;

    // Public so callers like the /api/anna/mirror-health endpoint check the
    // exact same domains this transport actually uses, instead of maintaining
    // a second hardcoded copy that drifts out of sync.
    public static readonly string[] BaseDomains =
    {
        "https://annas-archive.gl",
        "https://annas-archive.pk",
        "https://annas-archive.gd"
    };

    public AnnasArchiveTransport(HttpClient http, Func<string, Task<string>>? playwrightFetcher = null)
    {
        _http = http;
        _playwrightFetcher = playwrightFetcher;
    }

    /// <summary>The shared client, exposed for streaming a response body.</summary>
    public HttpClient HttpClient => _http;

    public async Task<string> GetStringAsync(string pathAndQuery)
    {
        // Use Playwright fetcher if available (bypasses Cloudflare)
        if (_playwrightFetcher != null)
        {
            var fallbackSw = Stopwatch.StartNew();
            // Try each domain with Playwright — sequential by necessity today
            // (no racing), so each failed/slow domain pays its full latency
            // before the next is even attempted. This loop's total duration
            // is exactly that cost.
            foreach (var domain in BaseDomains)
            {
                var domainSw = Stopwatch.StartNew();
                try
                {
                    var url = $"{domain}{pathAndQuery}";
                    var html = await _playwrightFetcher(url);
                    if (!string.IsNullOrEmpty(html) && !html.Contains("challenge-running"))
                    {
                        Console.WriteLine($"[AnnasArchive] Playwright successfully fetched from {domain}");
                        PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, true, ("Domain", domain));
                        PerfLog.Record("AnnasArchive.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, true, ("WinningDomain", domain));
                        return html;
                    }
                    PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Reason", "empty or challenge page"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AnnasArchive] Playwright failed for {domain}: {ex.Message}");
                    PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Error", ex.Message));
                }
            }
            // Fall through to HttpClient if Playwright fails for all domains
            Console.WriteLine("[AnnasArchive] Playwright failed for all domains, falling back to HttpClient");
            PerfLog.Record("AnnasArchive.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, false, ("Reason", "all domains failed via Playwright"));
        }

        using var resp = await GetAsync(pathAndQuery);
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task<T?> GetJsonAsync<T>(string pathAndQuery)
    {
        using var resp = await GetAsync(pathAndQuery);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    public async Task<JsonElement> GetJsonElementAsync(string pathAndQuery)
    {
        using var resp = await GetAsync(pathAndQuery);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Fetches an absolute URL rather than a mirror path. If the URL already
    /// points at a mirror, the same path is retried against every mirror in
    /// turn; if it points anywhere else (a third-party file host, which is
    /// what fast-download links usually are) it is fetched as given.
    /// Returns null when nothing succeeded — this is a best-effort fetch, not
    /// an error path, so it deliberately does not throw.
    /// </summary>
    public async Task<HttpResponseMessage?> GetAbsoluteAsync(
        string url,
        HttpCompletionOption completionOption)
    {
        foreach (var candidate in BuildFallbackUris(url))
        {
            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.GetAsync(candidate, completionOption);
                if (resp.IsSuccessStatusCode)
                    return resp;
            }
            catch
            {
                resp?.Dispose();
                continue;
            }

            resp?.Dispose();
        }

        return null;
    }

    internal static IEnumerable<Uri> BuildFallbackUris(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Enumerable.Empty<Uri>();

        if (!IsAnnasArchiveHost(uri.Host))
            return new[] { uri };

        return BaseDomains.Select(domain => new Uri($"{domain}{uri.PathAndQuery}"));
    }

    internal static bool IsAnnasArchiveHost(string host)
    {
        return BaseDomains.Any(domain =>
            host.EndsWith(new Uri(domain).Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tries every mirror in order and returns the first success. Throws when
    /// all of them fail, so callers that need a response can rely on getting
    /// one or an exception, never a null.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(string pathAndQuery)
    {
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;
        var fallbackSw = Stopwatch.StartNew();

        for (var i = 0; i < BaseDomains.Length; i++)
        {
            var domain = BaseDomains[i];
            var uri = new Uri($"{domain}{pathAndQuery}");
            var domainSw = Stopwatch.StartNew();
            try
            {
                var resp = await _http.GetAsync(uri);
                if (resp.IsSuccessStatusCode)
                {
                    if (i > 0)
                    {
                        // Log successful fallback
                        Console.WriteLine($"[AnnasArchive] Successfully connected via fallback domain: {domain}");
                    }
                    PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, true, ("Domain", domain), ("Via", "HttpClient"));
                    PerfLog.Record("AnnasArchive.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, true, ("WinningDomain", domain), ("Via", "HttpClient"));
                    return resp;
                }

                lastResponse?.Dispose();
                lastResponse = resp;
                PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Via", "HttpClient"), ("StatusCode", (int)resp.StatusCode));
                Console.WriteLine($"[AnnasArchive] Domain {domain} returned {(int)resp.StatusCode}, trying next...");
            }
            catch (Exception ex)
            {
                lastException = ex;
                PerfLog.Record("AnnasArchive.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Via", "HttpClient"), ("Error", ex.Message));
                Console.WriteLine($"[AnnasArchive] Domain {domain} failed: {ex.Message}, trying next...");
                // continue to next domain
            }
        }

        PerfLog.Record("AnnasArchive.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, false, ("Via", "HttpClient"), ("Reason", "all domains failed"));

        if (lastResponse != null)
        {
            var status = (int)lastResponse.StatusCode;
            lastResponse.Dispose();
            throw new HttpRequestException($"Request failed with status {status}");
        }

        throw new HttpRequestException(
            $"Request failed for all Anna's Archive domains. Last error: {lastException?.Message ?? "Unknown"}");
    }
}
#nullable restore
