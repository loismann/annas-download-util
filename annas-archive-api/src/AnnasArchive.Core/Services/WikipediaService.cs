using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AnnasArchive.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.Core.Services;

public class WikipediaService : IWikipediaService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan DescriptionCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan NotFoundCacheDuration = TimeSpan.FromHours(4);

    public WikipediaService(IHttpClientFactory httpFactory, IMemoryCache cache)
    {
        _httpFactory = httpFactory;
        _cache = cache;
    }

    public async Task<string?> GetBookDescriptionAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var cacheKey = $"wikipedia_desc_{title.Trim().ToLowerInvariant()}_{author?.Trim().ToLowerInvariant() ?? ""}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            PerfLog.Record("Wikipedia.GetBookDescription", 0, cached != null, ("Title", title), ("CacheHit", true));
            return cached;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var http = _httpFactory.CreateClient("Wikipedia");
            http.Timeout = TimeSpan.FromSeconds(4);

            var searchQuery = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";
            var searchUrl = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(searchQuery)}&format=json&srlimit=3";

            using var searchResponse = await http.GetAsync(searchUrl);
            if (!searchResponse.IsSuccessStatusCode)
            {
                CacheNegative(cacheKey);
                PerfLog.Record("Wikipedia.GetBookDescription", sw.Elapsed.TotalMilliseconds, false, ("Title", title), ("Reason", "search request failed"));
                return null;
            }

            using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
            using var searchDoc = await JsonDocument.ParseAsync(searchStream);

            if (!searchDoc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("search", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                CacheNegative(cacheKey);
                PerfLog.Record("Wikipedia.GetBookDescription", sw.Elapsed.TotalMilliseconds, false, ("Title", title), ("Reason", "no search results"));
                return null;
            }

            // Try each candidate in order — skip disambiguation pages, which
            // are just lists of options rather than a real article.
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("title", out var titleProp))
                    continue;

                var pageTitle = titleProp.GetString();
                if (string.IsNullOrWhiteSpace(pageTitle))
                    continue;

                var summaryUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(pageTitle)}";
                using var summaryResponse = await http.GetAsync(summaryUrl);
                if (!summaryResponse.IsSuccessStatusCode)
                    continue;

                using var summaryStream = await summaryResponse.Content.ReadAsStreamAsync();
                using var summaryDoc = await JsonDocument.ParseAsync(summaryStream);
                var root = summaryDoc.RootElement;

                var pageType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (string.Equals(pageType, "disambiguation", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!root.TryGetProperty("extract", out var extractProp))
                    continue;

                var extract = extractProp.GetString();
                if (string.IsNullOrWhiteSpace(extract) || extract.Length < 40)
                    continue;

                _cache.Set(cacheKey, extract, DescriptionCacheDuration);
                Log.Information("[Wikipedia] Found description for '{Title}' via page '{PageTitle}'", title, pageTitle);
                PerfLog.Record("Wikipedia.GetBookDescription", sw.Elapsed.TotalMilliseconds, true, ("Title", title), ("PageTitle", pageTitle));
                return extract;
            }

            CacheNegative(cacheKey);
            PerfLog.Record("Wikipedia.GetBookDescription", sw.Elapsed.TotalMilliseconds, false, ("Title", title), ("Reason", "no usable candidate page"));
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning("[Wikipedia] Lookup failed for '{Title}': {Message}", title, ex.Message);
            CacheNegative(cacheKey);
            PerfLog.Record("Wikipedia.GetBookDescription", sw.Elapsed.TotalMilliseconds, false, ("Title", title), ("Error", ex.Message));
            return null;
        }
    }

    private void CacheNegative(string cacheKey) =>
        _cache.Set(cacheKey, (string?)null, NotFoundCacheDuration);
}
