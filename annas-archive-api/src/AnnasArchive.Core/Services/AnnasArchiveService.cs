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
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Searching Anna's Archive, and finding covers for what it returns. Fetching
/// lives in <see cref="AnnasArchiveTransport"/> and downloading in
/// <see cref="AnnasArchiveDownloads"/>.
/// </summary>
public class AnnasArchiveService
{
    private readonly IMemoryCache _cache;
    private readonly AnnasArchiveTransport _transport;
    public HttpClient HttpClient => _transport.HttpClient;   // expose for streaming

    // Short TTL — this is purely to avoid re-scraping (several seconds
    // through Playwright/Cloudflare) for an identical repeated search
    // moments later, e.g. re-opening a search, adjusting a client-side
    // filter that doesn't change the query, or a stray double-submit. Not
    // meant to serve stale results for long.
    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(5);

    public AnnasArchiveService(HttpClient http, IMemoryCache cache, Func<string, Task<string>>? playwrightFetcher = null)
        : this(new AnnasArchiveTransport(http, playwrightFetcher), cache)
    {
    }

    public AnnasArchiveService(AnnasArchiveTransport transport, IMemoryCache cache)
    {
        _transport = transport;
        _cache = cache;
    }

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
        var advancedQuery = AnnasArchiveHtmlParser.BuildSearchQuery(trimmedQuery, exact);
        var effectiveQuery = advancedQuery;
        var fallbackAttempted = false;

        /* 1️⃣  keep fetching pages until we have >= limit books or no more pages */
        while (collected.Count < limit)
        {
            var html = await _transport.GetStringAsync(
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
                    $"[AnnasArchiveService] 0 book containers found for query='{effectiveQuery}' page={page - 1} " +
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
                    Console.WriteLine($"[AnnasArchiveService] Dumped raw HTML to {debugPath}");
                }
                catch (Exception dumpEx)
                {
                    Console.WriteLine($"[AnnasArchiveService] Failed to dump debug HTML: {dumpEx.Message}");
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

                var dto = AnnasArchiveHtmlParser.BuildDtoFromAnchor(container, md5);

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
                Console.WriteLine($"[AnnasArchiveService] Failed to build DTO for container index={index}: {ex}");
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

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private static readonly LruCache<string, string?> IsbnCoverCache =
        new(capacity: 2000, ttl: TimeSpan.FromHours(12));

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
        if (IsbnCoverCache.TryGetValue(key, out var cachedCoverUrl))
            return cachedCoverUrl;

        string? coverUrl = null;
        try
        {
            var html = await _transport.GetStringAsync($"/md5/{key}");
            var match = IsbnRx.Match(html);
            if (match.Success)
            {
                var isbn = match.Groups[1].Value.Replace("-", "");
                coverUrl = $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false";
                Console.WriteLine($"[AnnasArchiveService] GetCoverByMd5Async md5={key} found ISBN={isbn} coverUrl={coverUrl}");
            }
            else
            {
                Console.WriteLine($"[AnnasArchiveService] GetCoverByMd5Async md5={key} no ISBN found on detail page (htmlLength={html.Length})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnnasArchiveService] GetCoverByMd5Async md5={key} failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Negative results are cached too: a book with no ISBN on its detail
        // page would otherwise re-fetch that page on every single render.
        IsbnCoverCache.Set(key, coverUrl);

        return coverUrl;
    }

}
#nullable restore
