#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnnasArchive.Core.Helpers;
using AnnasArchive.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Cover lookup against Anna's Archive detail pages. Fetching lives in
/// <see cref="AnnasArchiveTransport"/> and downloading in
/// <see cref="AnnasArchiveDownloads"/>.
///
/// <para>This used to search as well. That went when Anna's moved behind
/// DDoS-Guard on 2026-08-13 and its HTML stopped answering a scraper: search is
/// LibGen's now (see <c>BookSearch</c>), and the last caller of the Anna's
/// scraper — the related-books series expansion — moved across with it. What is
/// left is the one thing only Anna's does, which is resolve an md5 to an ISBN and
/// therefore to a cover. It is the sole remaining user of the Playwright
/// bypass.</para>
/// </summary>
public class AnnasArchiveService
{
    private readonly IMemoryCache _cache;
    private readonly AnnasArchiveTransport _transport;
    public HttpClient HttpClient => _transport.HttpClient;   // expose for streaming

    public AnnasArchiveService(HttpClient http, IMemoryCache cache, Func<string, Task<string>>? playwrightFetcher = null)
        : this(new AnnasArchiveTransport(http, playwrightFetcher), cache)
    {
    }

    public AnnasArchiveService(AnnasArchiveTransport transport, IMemoryCache cache)
    {
        _transport = transport;
        _cache = cache;
    }

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private static readonly LruCache<string, string?> IsbnCoverCache =
        new(capacity: 2000, ttl: TimeSpan.FromHours(12));

    /// <summary>
    /// Looks up a book's cover via its ISBN, extracted from Anna's Archive's
    /// detail page, resolved against OpenLibrary's cover CDN (which is a
    /// separate, independently-reliable service from OpenLibrary's search
    /// API — the latter can be down without affecting this).
    ///
    /// <para>Called lazily, per book, on demand from the frontend once results
    /// have already rendered. Never in a loop over a result set: each call is a
    /// detail-page fetch through Playwright and costs seconds.</para>
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
                Log.Information("[AnnasArchiveService] GetCoverByMd5Async md5={Md5} found ISBN={Isbn} coverUrl={CoverUrl}", key, isbn, coverUrl);
            }
            else
            {
                Log.Information("[AnnasArchiveService] GetCoverByMd5Async md5={Md5} no ISBN found on detail page (htmlLength={HtmlLength})", key, html.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AnnasArchiveService] GetCoverByMd5Async md5={Md5} failed", key);
        }

        // Negative results are cached too: a book with no ISBN on its detail
        // page would otherwise re-fetch that page on every single render.
        IsbnCoverCache.Set(key, coverUrl);

        return coverUrl;
    }

}
#nullable restore
