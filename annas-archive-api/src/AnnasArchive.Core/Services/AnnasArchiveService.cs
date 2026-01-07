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
using HtmlAgilityPack;
using AnnasArchive.Core.Models;

namespace AnnasArchive.Core.Services;

public class AnnaArchiveService
{
    private readonly HttpClient _http;
    public HttpClient HttpClient => _http;               // expose for streaming

    public AnnaArchiveService(HttpClient http) => _http = http;

    private static readonly string[] BaseDomains =
    {
        "https://annas-archive.org",
        "https://annas-archive.li",
        "https://annas-archive.se",
        "https://annas-archive.pm",
        "https://annas-archive.in"
    };

    private const int MaxDetailFetches = 5;
    private static readonly TimeSpan DetailCacheTtl = TimeSpan.FromHours(12);
    private static readonly Dictionary<string, (DateTime fetchedAt, string? isbn, string? cover)> DetailCache = new();
    private static readonly object DetailCacheLock = new();

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private static readonly Regex ImgRx =
        new(@"https://[^""']+/covers[0-9]*/[^""']+\.jpg", RegexOptions.IgnoreCase);

    public async Task<IEnumerable<BookDto>> SearchAsync(string query, int limit = 50, bool exact = false)
    {
        if (limit <= 0)
            return Enumerable.Empty<BookDto>();

        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return Enumerable.Empty<BookDto>();

        var collected = new List<HtmlNode>();   // parent containers for each book
        var page = 1;
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

        /* 3️⃣  build DTOs in parallel */
        var sem   = new SemaphoreSlim(6);
        var tasks = collected.Select(async (container, index) =>
        {
            try
            {
                // Get MD5 from the cover link (first child <a> with /md5/)
                var coverLink = container.SelectSingleNode("./a[contains(@href,'/md5/')]");
                if (coverLink == null) return null;

                var md5 = Path.GetFileName(coverLink.GetAttributeValue("href", ""))
                            .ToLowerInvariant();

                var dto = BuildDtoFromAnchor(container, md5);

                if (index < MaxDetailFetches)
                {
                    await sem.WaitAsync();
                    try
                    {
                        var (isbn, cover) = await GetIsbnAndCoverAsync(md5);
                        dto.Isbn = isbn;

                        // Prioritize Open Library covers - they typically have proper 2:3 aspect ratio
                        // Standard ebook cover size is 1600×2560px (2:3 ratio)
                        if (!string.IsNullOrEmpty(isbn))
                        {
                            // Insert Open Library cover at the front - most likely to have correct dimensions
                            dto.CoverCandidates.Insert(0,
                                $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false");
                        }

                        if (!string.IsNullOrEmpty(cover))
                        {
                            // Anna's Archive detail page cover as second priority
                            var insertPosition = string.IsNullOrEmpty(isbn) ? 0 : 1;
                            dto.CoverCandidates.Insert(insertPosition, cover);
                        }
                    }
                    finally { sem.Release(); }
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
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
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

    private async Task<(string? isbn, string? cover)> GetIsbnAndCoverAsync(string md5)
    {
        if (TryGetCachedDetails(md5, out var cached))
            return cached;

        var html = await GetStringWithFallbackAsync($"/md5/{md5}");

        string? isbn = null;
        var m = IsbnRx.Match(html);
        if (m.Success)
            isbn = m.Groups[1].Value.Replace("-", "");

        string? cover = null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        cover = doc.DocumentNode
                   .SelectSingleNode("//img[contains(@class,'cover') or @itemprop='image']")
                   ?.GetAttributeValue("src", null);

        if (string.IsNullOrEmpty(cover))
        {
            var cm = ImgRx.Match(html);
            if (cm.Success) cover = cm.Value;
        }

        SetCachedDetails(md5, isbn, cover);
        return (isbn, cover);
    }

    private static bool TryGetCachedDetails(string md5, out (string? isbn, string? cover) cached)
    {
        lock (DetailCacheLock)
        {
            if (DetailCache.TryGetValue(md5, out var entry))
            {
                if (DateTime.UtcNow - entry.fetchedAt <= DetailCacheTtl)
                {
                    cached = (entry.isbn, entry.cover);
                    return true;
                }
                DetailCache.Remove(md5);
            }
        }

        cached = (null, null);
        return false;
    }

    private static void SetCachedDetails(string md5, string? isbn, string? cover)
    {
        lock (DetailCacheLock)
        {
            DetailCache[md5] = (DateTime.UtcNow, isbn, cover);
        }
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

        var fanOut = $"{md5[..2]}/{md5.Substring(2, 2)}/{md5.Substring(4, 2)}/{md5}.jpg";

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

        dto.CoverCandidates.AddRange(new[]
        {
            $"https://covers.zlibcdn2.com/covers/{fanOut}",
            $"https://covers.zlibcdn.com/covers/{md5}.jpg",
            $"{BaseDomains[0]}/covers/{md5}.jpg",
            $"{BaseDomains[1]}/covers/{md5}.jpg",
            $"{BaseDomains[2]}/covers/{md5}.jpg"
        });

        return dto;
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
        return host.EndsWith("annas-archive.org", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("annas-archive.li", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("annas-archive.se", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("annas-archive.pm", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("annas-archive.in", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringWithFallbackAsync(string pathAndQuery)
    {
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
        foreach (var domain in BaseDomains)
        {
            var uri = new Uri($"{domain}{pathAndQuery}");
            try
            {
                var resp = await _http.GetAsync(uri);
                if (resp.IsSuccessStatusCode)
                    return resp;

                lastResponse?.Dispose();
                lastResponse = resp;
            }
            catch
            {
                // ignore and try next domain
            }
        }

        if (lastResponse != null)
        {
            var status = (int)lastResponse.StatusCode;
            lastResponse.Dispose();
            throw new HttpRequestException($"Request failed with status {status}");
        }

        throw new HttpRequestException("Request failed for all Anna's Archive domains.");
    }
}
#nullable restore
