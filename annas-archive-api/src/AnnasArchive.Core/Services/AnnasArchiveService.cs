#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using HtmlAgilityPack;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace AnnasArchive.Core.Services;

public class AnnaArchiveService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly Func<string, Task<string>>? _playwrightFetcher;
    public HttpClient HttpClient => _http;               // expose for streaming

    // Short TTL — this is purely to avoid re-scraping (several seconds
    // through Playwright/Cloudflare) for an identical repeated search
    // moments later, e.g. re-opening a search, adjusting a client-side
    // filter that doesn't change the query, or a stray double-submit. Not
    // meant to serve stale results for long.
    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(5);

    public AnnaArchiveService(HttpClient http, IMemoryCache cache, Func<string, Task<string>>? playwrightFetcher = null)
    {
        _http = http;
        _cache = cache;
        _playwrightFetcher = playwrightFetcher;
    }

    // Public so callers like the /api/anna/mirror-health endpoint check the
    // exact same domains this service actually uses, instead of maintaining
    // a second hardcoded copy that drifts out of sync.
    public static readonly string[] BaseDomains =
    {
        "https://annas-archive.gl",
        "https://annas-archive.pk",
        "https://annas-archive.gd"
    };

    /// <summary>
    /// <paramref name="startPage"/> lets a caller resume scraping from a
    /// specific Anna's Archive results page instead of always starting at
    /// page 1 — used to split a search into a fast first batch (page 1,
    /// returned to the caller immediately) followed by a background
    /// continuation request (startPage: 2) for the rest, instead of
    /// blocking one HTTP response on fetching everything up front.
    /// </summary>
    public async Task<IEnumerable<BookDto>> SearchAsync(string query, int limit = 50, bool exact = false, int startPage = 1)
    {
        if (limit <= 0)
            return Enumerable.Empty<BookDto>();

        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return Enumerable.Empty<BookDto>();

        var cacheKey = $"annasearch_{trimmedQuery.ToLowerInvariant()}_{limit}_{exact}_{startPage}";
        if (_cache.TryGetValue(cacheKey, out List<BookDto>? cached))
            return cached!;

        var collected = new List<HtmlNode>();   // parent containers for each book
        var page = Math.Max(1, startPage);
        var advancedQuery = BuildSearchQuery(trimmedQuery, exact);
        var effectiveQuery = advancedQuery;
        var fallbackAttempted = false;

        /* 1️⃣  keep fetching pages until we have >= limit books or no more pages */
        while (collected.Count < limit)
        {
            var html = await GetStringWithFallbackAsync(
                $"/search?index=&page={page}&q={Uri.EscapeDataString(effectiveQuery)}&display=&sort=");
            page++;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Find the book containers
            // Each book is in a div with class "flex pt-3 pb-3 border-b"
            var bookContainers = doc.DocumentNode
                .SelectNodes("//div[contains(@class,'flex') and contains(@class,'pt-3') and contains(@class,'pb-3') and contains(@class,'border-b')]")?
                .ToList() ?? new();

            if (bookContainers.Count == 0)
            {
                // Diagnostic: distinguishes "genuinely no results" from "the page
                // structure/selectors no longer match" — a Cloudflare challenge
                // page and a changed Anna's Archive layout both land here silently
                // otherwise, and look identical to a real empty result from the
                // caller's perspective.
                var looksLikeChallenge = html.Contains("challenge-running", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("cf-spinner", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(
                    $"[AnnaArchiveService] 0 book containers found for query='{effectiveQuery}' page={page - 1} " +
                    $"htmlLength={html.Length} looksLikeCloudflareChallenge={looksLikeChallenge}");

                // Dump the raw HTML so it can be inspected directly — this is a
                // temporary diagnostic for tracking down selector drift, safe to
                // remove once parsing is confirmed fixed against the real page.
                try
                {
                    var debugDir = Path.Combine(AppContext.BaseDirectory, "logs");
                    Directory.CreateDirectory(debugDir);
                    var debugPath = Path.Combine(debugDir, $"debug-search-empty-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html");
                    File.WriteAllText(debugPath, html);
                    Console.WriteLine($"[AnnaArchiveService] Dumped raw HTML to {debugPath}");
                }
                catch (Exception dumpEx)
                {
                    Console.WriteLine($"[AnnaArchiveService] Failed to dump debug HTML: {dumpEx.Message}");
                }

                if (!fallbackAttempted && !string.Equals(effectiveQuery, trimmedQuery, StringComparison.Ordinal) && page == 2)
                {
                    // Fallback to basic query if advanced syntax yields no results.
                    effectiveQuery = trimmedQuery;
                    page = 1;
                    fallbackAttempted = true;
                    continue;
                }
                break;      // ran out of pages
            }
            collected.AddRange(bookContainers);
        }

        /* 2️⃣  trim to the requested limit */
        collected = collected.Take(limit).ToList();

        /* 3️⃣  build DTOs — no per-book network calls here anymore. This used
         * to fetch each book's full detail page (ISBN + cover) synchronously
         * for the first 5 results, which for Anna's Archive results routes
         * through the Playwright/Cloudflare-bypass browser and could add
         * several seconds per book, blocking the entire search response.
         * The frontend already has its own lazy, staggered cover-lookup
         * fallback (queueCoverLookups/lookupCoverForBook in
         * book-search.component.ts) that kicks in per-book after results
         * render, so there's no need to block search on this at all — we
         * just grab whatever thumbnail is already sitting in the search
         * listing HTML for free (zero extra requests) and let the frontend
         * fill in the rest lazily. */
        var results = collected.Select((container, index) =>
        {
            try
            {
                // Get MD5 from the cover link (first child <a> with /md5/)
                var coverLink = container.SelectSingleNode("./a[contains(@href,'/md5/')]");
                if (coverLink == null) return null;

                var md5 = Path.GetFileName(coverLink.GetAttributeValue("href", ""))
                            .ToLowerInvariant();

                var dto = BuildDtoFromAnchor(container, md5);

                // Free thumbnail, if the listing page includes one — no extra
                // request needed, it's already part of the HTML we fetched.
                var thumbSrc = coverLink.SelectSingleNode(".//img")?.GetAttributeValue("src", null);
                if (!string.IsNullOrWhiteSpace(thumbSrc))
                {
                    dto.CoverCandidates.Add(thumbSrc);
                }

                var deduped = dto.CoverCandidates
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();

                dto.CoverCandidates.Clear();
                dto.CoverCandidates.AddRange(deduped);

                return dto;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnaArchiveService] Failed to build DTO for container index={index}: {ex}");
                return null;
            }
        });

        var finalResults = results.Where(r => r != null).ToList()!;
        _cache.Set(cacheKey, finalResults, SearchCacheDuration);
        return finalResults;
    }

    /// <summary>
    /// Cover lookup for books with only a title/author (no MD5) — e.g. the AI
    /// Related Books flow, which deals in GPT-suggested titles that don't
    /// exist as an Anna's Archive result yet. Reuses the free thumbnail
    /// already embedded in search listing HTML (populated in SearchAsync
    /// above) instead of Google Books (quota exhausted) or OpenLibrary's
    /// search API (down) — no new external dependency, and no per-book
    /// detail-page fetch, just whatever's already sitting in one small
    /// search response.
    /// </summary>
    public async Task<string?> GetCoverByTitleAuthorAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var query = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";
        var results = await SearchAsync(query, limit: 5, exact: false);
        return results.FirstOrDefault(r => r.CoverCandidates.Count > 0)?.CoverCandidates.FirstOrDefault();
    }

    private static string BuildSearchQuery(string query, bool exact)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        if (exact)
            return $"\"{trimmed}\"";

        return trimmed;
    }

    private static BookDto BuildDtoFromAnchor(HtmlNode container, string md5)
    {
        // New HTML structure:
        // <a class="line-clamp-[3] ... js-vim-focus">TITLE</a>
        // <a class="line-clamp-[2] ... text-sm" href="/search?q=AUTHORS">AUTHORS</a>
        // <a class="line-clamp-[2] ... text-sm" href="/search?q=PUBLISHER">PUBLISHER, SERIES, YEAR</a>
        // <div class="text-gray-800 dark:text-slate-400 ...">LANG [code] · FORMAT · SIZE · YEAR · TYPE · SOURCES</div>

        // Extract title
        var titleNode = container.SelectSingleNode(".//a[contains(@class,'js-vim-focus')]")
            ?? container.SelectSingleNode(".//a[contains(@class,'line-clamp') and not(.//img)]")
            ?? container.SelectSingleNode(".//a[contains(@href,'/md5/') and string-length(normalize-space(text()))>0]")
            ?? container.SelectSingleNode(".//a[not(contains(@href,'/search')) and string-length(normalize-space(text()))>0]")
            ?? container.SelectSingleNode(".//a[contains(@href,'/md5/')]");

        var rawTitle = titleNode?.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(rawTitle))
            rawTitle = titleNode?.GetAttributeValue("title", null);
        if (string.IsNullOrWhiteSpace(rawTitle))
            rawTitle = $"Unknown Title ({md5})";

        var title = HtmlEntity.DeEntitize(rawTitle);

        // Extract authors (has user-edit icon)
        var authorNode = container.SelectSingleNode(".//a[contains(@class,'text-sm')]/span[contains(@class,'icon-[mdi--user-edit]')]/parent::a");
        if (authorNode == null)
        {
            authorNode = container.SelectNodes(".//a[contains(@class,'text-sm') and contains(@href,'/search')]")
                ?.FirstOrDefault();
        }
        var rawAuthorText = authorNode?.InnerText?.Trim() ?? "";
        var authorText = HtmlEntity.DeEntitize(rawAuthorText);
        var authors = string.IsNullOrEmpty(authorText)
            ? new List<string>()
            : authorText.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(a => a.Trim())
                       .Where(a => a.Length > 0)
                       .ToList();

        // Extract publisher/series/year (has company icon)
        var publisherNode = container.SelectSingleNode(".//a[contains(@class,'text-sm')]/span[contains(@class,'icon-[mdi--company]')]/parent::a");
        if (publisherNode == null)
        {
            publisherNode = container.SelectNodes(".//a[contains(@class,'text-sm') and contains(@href,'/search')]")
                ?.Skip(1)
                .FirstOrDefault();
        }
        var rawPublisherText = publisherNode?.InnerText?.Trim() ?? "";
        var publisherText = HtmlEntity.DeEntitize(rawPublisherText);

        // Parse publisher text: "Publisher, Series X, Year"
        var publisherParts = publisherText.Split(',').Select(p => p.Trim()).ToArray();
        var publisher = publisherParts.ElementAtOrDefault(0) ?? "";

        int? year = null;
        foreach (var part in publisherParts.Reverse())
        {
            if (int.TryParse(part, out var y) && y > 1000 && y < 3000)
            {
                year = y;
                break;
            }
        }

        // Extract metadata line: "English [en] · MOBI · 0.3MB · 2015 · 📕 Book (fiction) · 🚀/lgli/lgrs/upload/zlib"
        var metadataNode = container.SelectSingleNode(".//div[contains(@class,'text-gray-800') or contains(@class,'text-slate-400')]");
        var rawMetadataText = metadataNode?.InnerText?.Trim() ?? "";
        var metadataText = HtmlEntity.DeEntitize(rawMetadataText);

        var metaParts = metadataText.Split('·').Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

        var language = "";
        var format = "";
        var fileSize = "";
        var bookType = "";
        var source = "";

        foreach (var part in metaParts)
        {
            if (part.Contains("[") && part.Contains("]"))
            {
                // Language: "English [en]"
                language = part.Split('[')[0].Trim();
            }
            else if (part.Contains("Book") || part.Contains("book") || part.Contains("📕") || part.Contains("magazine") || part.Contains("comic"))
            {
                // Book type: "📕 Book (fiction)", "Book (non-fiction)", "magazine"
                // Check this BEFORE fileSize because "Book" might contain "B"
                bookType = part.Replace("📕", "").Replace("🚀", "").Trim();
            }
            else if (part.Contains("MB") || part.Contains("KB") || part.Contains("GB"))
            {
                // File size: "0.3MB", "125KB", "1.2GB"
                fileSize = part;
            }
            else if (part.Contains("/"))
            {
                // Source: "/lgli/lgrs/upload/zlib" or "🚀/lgli/lgrs/upload/zlib"
                source = part.Replace("🚀", "").Trim();
            }
            else if (int.TryParse(part, out var y) && y > 1000 && y < 3000 && year == null)
            {
                // Year in metadata if not found in publisher
                year = y;
            }
            else if (string.IsNullOrEmpty(format) && part.Length <= 10 && !part.Contains(" "))
            {
                // Format: "MOBI", "PDF", "EPUB", "AZW3", etc.
                // This should be the first unmatched short token without spaces
                format = part;
            }
        }

        var dto = new BookDto(
            title,
            md5,
            authors,
            language,
            format,
            source,
            fileSize,
            bookType,
            publisher,
            year,
            null,
            null
        );

        // No speculative guessed cover URLs here anymore — they were unverified
        // (zlibcdn/zlibcdn2 domains, arbitrary {domain}/covers/{md5}.jpg
        // patterns against the search domains) and confirmed dead via browser
        // testing (ERR_SSL_PROTOCOL_ERROR). Leaving CoverCandidates empty when
        // there's no free listing-page thumbnail lets the frontend's
        // needsExternalCoverLookup() correctly detect "needs a cover" and
        // immediately use the reliable MD5→ISBN→OpenLibrary-CDN lookup
        // (GetCoverByMd5Async) instead of wasting a cascade of failed
        // image loads on dead guesses first.

        return dto;
    }

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private const int IsbnCoverCacheLimit = 2000;
    private static readonly Dictionary<string, (DateTime fetchedAt, string? coverUrl)> IsbnCoverCache = new();
    private static readonly object IsbnCoverCacheLock = new();
    private static readonly TimeSpan IsbnCoverCacheTtl = TimeSpan.FromHours(12);

    /// <summary>
    /// Looks up a book's cover via its ISBN, extracted from Anna's Archive's
    /// detail page, resolved against OpenLibrary's cover CDN (which is a
    /// separate, independently-reliable service from OpenLibrary's search
    /// API — the latter can be down without affecting this). Deliberately
    /// NOT called from SearchAsync (that's what made search slow before) —
    /// this is meant to be called lazily, per-book, on demand from the
    /// frontend after search results have already rendered.
    /// </summary>
    public async Task<string?> GetCoverByMd5Async(string md5)
    {
        if (string.IsNullOrWhiteSpace(md5))
            return null;

        var key = md5.ToLowerInvariant();
        lock (IsbnCoverCacheLock)
        {
            if (IsbnCoverCache.TryGetValue(key, out var cached) &&
                DateTime.UtcNow - cached.fetchedAt <= IsbnCoverCacheTtl)
            {
                return cached.coverUrl;
            }
        }

        string? coverUrl = null;
        try
        {
            var html = await GetStringWithFallbackAsync($"/md5/{key}");
            var match = IsbnRx.Match(html);
            if (match.Success)
            {
                var isbn = match.Groups[1].Value.Replace("-", "");
                coverUrl = $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false";
                Console.WriteLine($"[AnnaArchiveService] GetCoverByMd5Async md5={key} found ISBN={isbn} coverUrl={coverUrl}");
            }
            else
            {
                Console.WriteLine($"[AnnaArchiveService] GetCoverByMd5Async md5={key} no ISBN found on detail page (htmlLength={html.Length})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnnaArchiveService] GetCoverByMd5Async md5={key} failed: {ex.GetType().Name}: {ex.Message}");
        }

        lock (IsbnCoverCacheLock)
        {
            // Simple unbounded-growth guard — this is a lazy best-effort cache,
            // not something that needs LRU precision.
            if (IsbnCoverCache.Count >= IsbnCoverCacheLimit)
                IsbnCoverCache.Clear();

            IsbnCoverCache[key] = (DateTime.UtcNow, coverUrl);
        }

        return coverUrl;
    }

    public async Task<List<string>> GetDownloadLinksAsync(string md5)
    {
        var links = await GetJsonWithFallbackAsync<List<string>>(
            $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}");
        return links ?? new List<string>();
    }

    public async Task<List<string>> GetMemberDownloadLinksAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0&domain_index=0";

        var doc = await GetJsonElementWithFallbackAsync(url);
        if (doc.ValueKind != JsonValueKind.Object) return new List<string>();

        var results = new List<string>();
        if (doc.TryGetProperty("download_url", out var token))
        {
            if (token.ValueKind == JsonValueKind.Array)
            {
                results.AddRange(token.EnumerateArray()
                                      .Select(e => e.GetString()!)
                                      .Where(s => !string.IsNullOrEmpty(s)));
            }
            else if (token.ValueKind == JsonValueKind.String)
            {
                var s = token.GetString();
                if (!string.IsNullOrEmpty(s)) results.Add(s);
            }
        }

        return results;
    }

    public async Task<JsonElement> GetMemberDownloadDocumentAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json"
                + $"?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0&domain_index=0";

        try
        {
            var doc = await GetJsonElementWithFallbackAsync(url);
            if (doc.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Failed to fetch download document.");
            return doc;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Rate limit exceeded. Please wait before trying again.", ex);
        }
    }

    /// <summary>
    /// Scrapes the Anna's Archive account page to get download counter information.
    /// Returns (downloadsLeft, downloadsPerDay) or null if not found.
    /// </summary>
    public async Task<(int downloadsLeft, int downloadsPerDay)?> GetDownloadCounterFromProfileAsync()
    {
        try
        {
            // Try the /account page first
            var html = await GetStringWithFallbackAsync("/account");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Look for download counter text patterns in the page
            // Common patterns: "X downloads left today", "X / Y downloads", "X out of Y", etc.
            var allText = doc.DocumentNode.InnerText;

            // Pattern 1: "X downloads left" or "X fast downloads left"
            var leftMatch = Regex.Match(allText, @"(\d+)\s+(?:fast\s+)?downloads?\s+left", RegexOptions.IgnoreCase);

            // Pattern 2: "X / Y" or "X out of Y" for downloads
            var ratioMatch = Regex.Match(allText, @"(\d+)\s*[/\\]\s*(\d+)\s+(?:fast\s+)?downloads?", RegexOptions.IgnoreCase);

            // Pattern 3: Look for specific elements that might contain the counter
            // Try to find divs or spans that mention "download" and contain numbers
            var downloadNodes = doc.DocumentNode.SelectNodes("//div[contains(translate(., 'DOWNLOAD', 'download'), 'download')] | //span[contains(translate(., 'DOWNLOAD', 'download'), 'download')] | //p[contains(translate(., 'DOWNLOAD', 'download'), 'download')]");

            int? downloadsLeft = null;
            int? downloadsPerDay = null;

            if (ratioMatch.Success && ratioMatch.Groups.Count >= 3)
            {
                // Found "X / Y downloads" pattern
                downloadsLeft = int.Parse(ratioMatch.Groups[1].Value);
                downloadsPerDay = int.Parse(ratioMatch.Groups[2].Value);
            }
            else if (leftMatch.Success)
            {
                // Found "X downloads left" pattern
                downloadsLeft = int.Parse(leftMatch.Groups[1].Value);

                // Try to find the total downloads per day
                var totalMatch = Regex.Match(allText, @"(\d+)\s+(?:fast\s+)?downloads?\s+per\s+day", RegexOptions.IgnoreCase);
                if (totalMatch.Success)
                {
                    downloadsPerDay = int.Parse(totalMatch.Groups[1].Value);
                }
            }

            // If we found at least the downloads left, return what we have
            if (downloadsLeft.HasValue && downloadsPerDay.HasValue)
            {
                return (downloadsLeft.Value, downloadsPerDay.Value);
            }

            return null;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid argument scraping download counter: {ex.ParamName}");
            return null;
        }
        catch (Exception ex)
        {
            // Log the error but don't crash
            Console.Error.WriteLine($"Failed to scrape download counter from profile: {ex.Message}");
            return null;
        }
    }

    public async Task<HttpResponseMessage?> GetDownloadResponseWithFallbackAsync(
        string downloadUrl,
        HttpCompletionOption completionOption)
    {
        var candidates = BuildDownloadFallbackUris(downloadUrl);
        foreach (var candidate in candidates)
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

    private static IEnumerable<Uri> BuildDownloadFallbackUris(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            return Enumerable.Empty<Uri>();

        if (!IsAnnaArchiveHost(uri.Host))
            return new[] { uri };

        return BaseDomains
            .Select(domain => new Uri($"{domain}{uri.PathAndQuery}"));
    }

    private static bool IsAnnaArchiveHost(string host)
    {
        return BaseDomains.Any(domain =>
            host.EndsWith(new Uri(domain).Host, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GetStringWithFallbackAsync(string pathAndQuery)
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

        using var resp = await GetWithFallbackAsync(pathAndQuery);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<T?> GetJsonWithFallbackAsync<T>(string pathAndQuery)
    {
        using var resp = await GetWithFallbackAsync(pathAndQuery);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    private async Task<JsonElement> GetJsonElementWithFallbackAsync(string pathAndQuery)
    {
        using var resp = await GetWithFallbackAsync(pathAndQuery);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc;
    }

    private async Task<HttpResponseMessage> GetWithFallbackAsync(string pathAndQuery)
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
