using System.Collections.Concurrent;
using System.Diagnostics;
using AnnasArchive.Core.Telemetry;
using Microsoft.Playwright;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Service that uses Playwright headless browser to bypass Cloudflare protection.
/// Maintains cookies that can be used by HttpClient for subsequent requests.
/// </summary>
public interface ICloudflareBypassService : IAsyncDisposable
{
    /// <summary>
    /// Gets valid cookies for the specified domain, solving Cloudflare challenge if needed.
    /// </summary>
    Task<IReadOnlyList<Cookie>> GetCookiesAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh of cookies for the specified domain.
    /// </summary>
    Task RefreshCookiesAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches HTML content using the browser, bypassing Cloudflare.
    /// Use this for pages that need JavaScript rendering or Cloudflare bypass.
    /// </summary>
    Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default);
}

public class Cookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public DateTime? Expires { get; set; }
}

public class CloudflareBypassService : ICloudflareBypassService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _browserLock;
    private readonly ConcurrentDictionary<string, (List<Cookie> Cookies, DateTime ExpiresAt)> _cookieCache = new();
    private readonly TimeSpan _cookieCacheDuration = TimeSpan.FromHours(1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;
    private readonly string? _proxyUrl;
    private readonly IVpnSettingsService _vpnSettings;

    public CloudflareBypassService(IConfiguration configuration, IVpnSettingsService vpnSettings)
    {
        _vpnSettings = vpnSettings;
        // Used to be a hard 1. Raising this helps only up to a point — Seq
        // data showed that going to 4 concurrent contexts against Anna's
        // Archive made EVERY fetch slower (individual fetches went from
        // 3-8s to 9-38s, several outright hit the 60s navigation timeout,
        // and DomainFallback totals hit 60-97s). That's consistent with
        // Cloudflare's anti-bot heuristics reacting to a burst of
        // simultaneous connections from one source, not a local CPU/RAM
        // limit — the NAS had plenty of headroom. 2 is a more conservative
        // middle ground: some overlap without looking like a burst.
        // Configurable via Playwright:MaxConcurrentContexts /
        // Playwright__MaxConcurrentContexts if this needs tuning again.
        var maxConcurrent = configuration.GetValue("Playwright:MaxConcurrentContexts", 2);
        _browserLock = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        // Routes this browser's traffic through the Gluetun/PIA proxy when
        // configured (AnnaArchiveProxy:Url, e.g. http://gluetun:8888) — this
        // is the part that actually matters for "only Anna's Archive goes
        // through the VPN", since the real scraping traffic goes through
        // this Playwright browser, not the plain HttpClient fallback.
        _proxyUrl = configuration["AnnaArchiveProxy:Url"];
    }

    private static readonly string[] UserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
    };

    public async Task<IReadOnlyList<Cookie>> GetCookiesAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalizedDomain = NormalizeDomain(domain);

        // Check cache first
        if (_cookieCache.TryGetValue(normalizedDomain, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Cookies.AsReadOnly();
        }

        await RefreshCookiesAsync(domain, cancellationToken);

        if (_cookieCache.TryGetValue(normalizedDomain, out var refreshed))
        {
            return refreshed.Cookies.AsReadOnly();
        }

        return Array.Empty<Cookie>();
    }

    public async Task RefreshCookiesAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var url = domain.StartsWith("http") ? domain : $"https://{domain}";

        Log.Information("[CloudflareBypass] Refreshing cookies for {Domain}...", normalizedDomain);

        var lockWaitSw = Stopwatch.StartNew();
        await _browserLock.WaitAsync(cancellationToken);
        PerfLog.Record("Playwright.LockWait", lockWaitSw.Elapsed.TotalMilliseconds, true, ("Caller", "RefreshCookies"), ("Domain", normalizedDomain));
        var opSw = Stopwatch.StartNew();
        try
        {
            await EnsureBrowserInitializedAsync();

            var context = await _browser!.NewContextAsync(BuildContextOptions());

            try
            {
                var page = await context.NewPageAsync();

                // Navigate and wait for Cloudflare challenge to complete
                var response = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 60000 // 60 second timeout for challenge
                });

                // Wait a bit for any JS to execute
                await page.WaitForTimeoutAsync(2000);

                // Check if we're still on a challenge page
                var content = await page.ContentAsync();
                if (content.Contains("challenge-running") || content.Contains("cf-spinner"))
                {
                    Log.Information("[CloudflareBypass] Challenge detected, waiting for completion...");
                    // Wait longer for challenge
                    await page.WaitForTimeoutAsync(5000);
                }

                // Extract cookies
                var playwrightCookies = await context.CookiesAsync();
                var cookies = playwrightCookies
                    .Where(c => c.Domain.Contains(normalizedDomain) || normalizedDomain.Contains(c.Domain.TrimStart('.')))
                    .Select(c => new Cookie
                    {
                        Name = c.Name,
                        Value = c.Value,
                        Domain = c.Domain,
                        Path = c.Path,
                        Expires = c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).UtcDateTime : null
                    })
                    .ToList();

                var cfClearance = cookies.FirstOrDefault(c => c.Name == "cf_clearance");
                if (cfClearance != null)
                {
                    Log.Information("[CloudflareBypass] Successfully obtained cf_clearance cookie for {Domain}", normalizedDomain);
                }
                else
                {
                    Log.Warning("[CloudflareBypass] No cf_clearance cookie found for {Domain}. Got {Count} other cookies.", normalizedDomain, cookies.Count);
                }

                _cookieCache[normalizedDomain] = (cookies, DateTime.UtcNow.Add(_cookieCacheDuration));
            }
            finally
            {
                await context.CloseAsync();
            }

            PerfLog.Record("Playwright.RefreshCookies", opSw.Elapsed.TotalMilliseconds, true, ("Domain", normalizedDomain));
        }
        catch (Exception ex)
        {
            PerfLog.Record("Playwright.RefreshCookies", opSw.Elapsed.TotalMilliseconds, false, ("Domain", normalizedDomain), ("Error", ex.Message));
            throw;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        Log.Debug("[CloudflareBypass] Fetching HTML for {Url}", url);

        var lockWaitSw = Stopwatch.StartNew();
        await _browserLock.WaitAsync(cancellationToken);
        // This is the number to watch — every concurrent search/AI request
        // funnels through the same single browser lock, so this is directly
        // "how much time did this request lose to contention" regardless of
        // how fast the fetch itself is.
        PerfLog.Record("Playwright.LockWait", lockWaitSw.Elapsed.TotalMilliseconds, true, ("Caller", "FetchHtml"), ("Url", url));
        var opSw = Stopwatch.StartNew();
        try
        {
            await EnsureBrowserInitializedAsync();

            var context = await _browser!.NewContextAsync(BuildContextOptions());

            try
            {
                var page = await context.NewPageAsync();

                var response = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 60000
                });

                // Wait for content to load
                await page.WaitForTimeoutAsync(1000);

                // Check for Cloudflare challenge
                var content = await page.ContentAsync();
                var retries = 0;
                while ((content.Contains("challenge-running") || content.Contains("cf-spinner") ||
                        content.Contains("Checking your browser")) && retries < 10)
                {
                    Log.Debug("[CloudflareBypass] Challenge in progress, waiting... (attempt {Attempt})", retries + 1);
                    await page.WaitForTimeoutAsync(2000);
                    content = await page.ContentAsync();
                    retries++;
                }

                if (response?.Status == 403)
                {
                    Log.Warning("[CloudflareBypass] Got 403 after challenge attempts for {Url}", url);
                }

                PerfLog.Record("Playwright.FetchHtml", opSw.Elapsed.TotalMilliseconds, true, ("Url", url), ("StatusCode", response?.Status));
                return content;
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            PerfLog.Record("Playwright.FetchHtml", opSw.Elapsed.TotalMilliseconds, false, ("Url", url), ("Error", ex.Message));
            throw;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task EnsureBrowserInitializedAsync()
    {
        if (_browser != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_browser != null) return;

            Log.Information("[CloudflareBypass] Initializing Playwright browser...");

            _playwright = await Playwright.CreateAsync();
            // No Proxy set here deliberately — proxy is applied per browser
            // CONTEXT instead (see BuildContextOptions), not at the browser
            // level, so the VPN on/off toggle can take effect per-request
            // on this one shared, long-lived browser instance instead of
            // requiring the whole browser to be relaunched.
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-accelerated-2d-canvas",
                    "--disable-gpu"
                }
            };

            _browser = await _playwright.Chromium.LaunchAsync(launchOptions);

            Log.Information("[CloudflareBypass] Browser initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Checks the live VPN toggle on every call — this, not the browser
    /// launch options, is what makes flipping the VPN on/off in the UI take
    /// effect on the very next request instead of requiring a restart.
    /// </summary>
    private BrowserNewContextOptions BuildContextOptions()
    {
        var options = new BrowserNewContextOptions
        {
            UserAgent = UserAgents[Random.Shared.Next(UserAgents.Length)],
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "en-US",
            TimezoneId = "America/New_York"
        };

        if (!string.IsNullOrWhiteSpace(_proxyUrl) && _vpnSettings.Current.Enabled)
        {
            options.Proxy = new Proxy { Server = _proxyUrl };
        }

        return options;
    }

    private static string NormalizeDomain(string domain)
    {
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri))
        {
            return uri.Host.Replace("www.", "");
        }
        return domain.Replace("www.", "").Replace("https://", "").Replace("http://", "").TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _initLock.Dispose();
        _browserLock.Dispose();
    }
}
