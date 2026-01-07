#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using AnnasArchive.Core.Models;

namespace AnnasArchive.Core.Services;

public class LibGenService
{
    private readonly HttpClient _http;
    public HttpClient HttpClient => _http;

    public LibGenService(HttpClient http) => _http = http;

    private static readonly string[] BaseDomains =
    {
        "https://libgen.bz",
        "https://libgen.la",
        "https://libgen.gl",
        "https://libgen.vg"
    };

    private const int MaxDetailFetches = 5;
    private static readonly TimeSpan DetailCacheTtl = TimeSpan.FromHours(12);
    private static readonly Dictionary<string, (DateTime fetchedAt, string? isbn, string? cover)> DetailCache = new();
    private static readonly object DetailCacheLock = new();

    public async Task<IEnumerable<BookDto>> SearchAsync(string query, int limit = 50, bool exact = false)
    {
        Console.WriteLine($"[LibGen] SearchAsync called with query='{query}', limit={limit}, exact={exact}");

        if (limit <= 0)
        {
            Console.WriteLine($"[LibGen] Invalid limit: {limit}");
            return Enumerable.Empty<BookDto>();
        }

        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            Console.WriteLine("[LibGen] Empty query after trim");
            return Enumerable.Empty<BookDto>();
        }

        Console.WriteLine($"[LibGen] Trying general search first...");
        // Try general search first (more reliable), then fall back to fiction search
        var generalResults = await SearchGeneralAsync(trimmedQuery, limit, exact);
        if (generalResults.Any())
        {
            Console.WriteLine($"[LibGen] General search returned {generalResults.Count()} results");
            return generalResults;
        }

        Console.WriteLine($"[LibGen] General search returned no results, trying fiction search...");
        var fictionResults = await SearchFictionAsync(trimmedQuery, limit, exact);
        Console.WriteLine($"[LibGen] Fiction search returned {fictionResults.Count()} results");
        return fictionResults;
    }

    private async Task<IEnumerable<BookDto>> SearchFictionAsync(string query, int limit, bool exact)
    {
        try
        {
            var searchQuery = exact ? $"\"{query}\"" : query;
            var url = $"/fiction/?q={Uri.EscapeDataString(searchQuery)}&criteria=&language=&format=";
            Console.WriteLine($"[LibGen Fiction] Fetching: {url}");

            var html = await GetStringWithFallbackAsync(url);
            Console.WriteLine($"[LibGen Fiction] Received HTML response, length: {html.Length}");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // LibGen fiction table structure: table with tbody containing book rows
            var rows = doc.DocumentNode
                .SelectNodes("//table[contains(@class, 'catalog')]//tbody/tr")?
                .Take(limit)
                .ToList() ?? new List<HtmlNode>();

            Console.WriteLine($"[LibGen Fiction] Found {rows.Count} table rows");

            if (rows.Count == 0)
            {
                Console.WriteLine("[LibGen Fiction] No rows found, trying alternative selector...");
                // Try alternative selectors
                var altRows = doc.DocumentNode
                    .SelectNodes("//table//tbody/tr")?
                    .Take(limit)
                    .ToList() ?? new List<HtmlNode>();
                Console.WriteLine($"[LibGen Fiction] Alternative selector found {altRows.Count} rows");

                if (altRows.Count == 0)
                {
                    // Log HTML snippet for debugging
                    var snippet = html.Length > 500 ? html.Substring(0, 500) : html;
                    Console.WriteLine($"[LibGen Fiction] HTML snippet: {snippet}...");
                }

                return Enumerable.Empty<BookDto>();
            }

            var books = new List<BookDto>();
            foreach (var row in rows)
            {
                var book = ParseFictionRow(row);
                if (book != null)
                {
                    Console.WriteLine($"[LibGen Fiction] Parsed book: {book.Title}");
                    books.Add(book);
                }
                else
                {
                    Console.WriteLine($"[LibGen Fiction] Failed to parse row");
                }
            }

            Console.WriteLine($"[LibGen Fiction] Returning {books.Count} books");
            return books;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibGen Fiction] ERROR: {ex.Message}");
            Console.WriteLine($"[LibGen Fiction] Stack trace: {ex.StackTrace}");
            return Enumerable.Empty<BookDto>();
        }
    }

    private async Task<IEnumerable<BookDto>> SearchGeneralAsync(string query, int limit, bool exact)
    {
        try
        {
            var searchQuery = exact ? $"\"{query}\"" : query;
            var url = $"/index.php?req={Uri.EscapeDataString(searchQuery)}&lg_topic=libgen&open=0&view=simple&res=25&phrase=1&column=def";
            Console.WriteLine($"[LibGen General] Fetching: {url}");

            var html = await GetStringWithFallbackAsync(url);
            Console.WriteLine($"[LibGen General] Received HTML response, length: {html.Length}");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // LibGen general search: table with class "c" containing book rows
            var rows = doc.DocumentNode
                .SelectNodes("//table[@id='tablelibgen']//tr[position()>1]")
                ?? doc.DocumentNode.SelectNodes("//table[contains(@rules, 'rows')]//tr[position()>1]")
                ?? doc.DocumentNode.SelectNodes("//table[@rules='rows']//tr[position()>1]");

            var rowList = rows?
                .Take(limit)
                .ToList() ?? new List<HtmlNode>();

            Console.WriteLine($"[LibGen General] Found {rowList.Count} table rows");

            if (rowList.Count == 0)
            {
                Console.WriteLine("[LibGen General] No rows found, trying alternative selector...");
                // Try alternative selectors
                var altRows = doc.DocumentNode
                    .SelectNodes("//table[@rules='rows']//tr[position()>1]")?
                    .Take(limit)
                    .ToList() ?? new List<HtmlNode>();
                Console.WriteLine($"[LibGen General] Alternative selector found {altRows.Count} rows");

                if (altRows.Count == 0)
                {
                    // Log HTML snippet for debugging
                    var snippet = html.Length > 500 ? html.Substring(0, 500) : html;
                    Console.WriteLine($"[LibGen General] HTML snippet: {snippet}...");
                }

                return Enumerable.Empty<BookDto>();
            }

            var books = new List<BookDto>();
            foreach (var row in rowList)
            {
                var book = ParseGeneralRow(row);
                if (book != null)
                {
                    Console.WriteLine($"[LibGen General] Parsed book: {book.Title}");
                    books.Add(book);
                }
                else
                {
                    Console.WriteLine($"[LibGen General] Failed to parse row");
                }
            }

            Console.WriteLine($"[LibGen General] Returning {books.Count} books");
            return books;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibGen General] ERROR: {ex.Message}");
            Console.WriteLine($"[LibGen General] Stack trace: {ex.StackTrace}");
            return Enumerable.Empty<BookDto>();
        }
    }

    private BookDto? ParseFictionRow(HtmlNode row)
    {
        try
        {
            var cells = row.SelectNodes("./td")?.ToList();
            if (cells == null || cells.Count < 9)
            {
                Console.WriteLine($"[LibGen Fiction Parse] Row has {cells?.Count ?? 0} cells, need at least 9");
                return null;
            }

            // Fiction table columns: Authors, Series, Title, Language, File, (mirrors)
            var authorsText = HtmlEntity.DeEntitize(cells[0].InnerText.Trim());
            var authors = ParseAuthors(authorsText);

            var title = HtmlEntity.DeEntitize(cells[2].InnerText.Trim());
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var language = HtmlEntity.DeEntitize(cells[3].InnerText.Trim());

            // File column contains format and size: "epub/653 Kb" or "pdf/1.2 Mb"
            var fileText = cells[4].InnerText.Trim().ToLowerInvariant();
            var format = "";
            var fileSize = "";

            if (fileText.Contains("/"))
            {
                var parts = fileText.Split('/');
                format = parts[0].Trim().ToUpperInvariant();
                fileSize = parts.Length > 1 ? parts[1].Trim().Replace(" ", "") : "";
            }

            // Extract MD5 from download link
            var downloadLink = cells[5].SelectSingleNode(".//a[contains(@href, '/main/')]");
            var md5 = ExtractMd5FromUrl(downloadLink?.GetAttributeValue("href", "") ?? "");

            if (string.IsNullOrEmpty(md5))
                return null;

            var dto = new BookDto(
                title,
                md5,
                authors,
                language,
                format,
                "libgen-fiction",
                fileSize,
                "Fiction",
                "",
                null,
                null,
                null
            );

            // LibGen cover URLs
            dto.CoverCandidates.AddRange(new[]
            {
                $"https://libgen.is/fictioncovers/{md5[0]}/{md5}.jpg",
                $"https://libgen.rs/fictioncovers/{md5[0]}/{md5}.jpg"
            });

            return dto;
        }
        catch
        {
            return null;
        }
    }

    private BookDto? ParseGeneralRow(HtmlNode row)
    {
        try
        {
            var cells = row.SelectNodes("./td")?.ToList();
            if (cells == null || cells.Count < 9)
                return null;

            // General table columns (new UI): Title/Series, Authors, Publisher, Year, Language, Pages, Size, Ext, Mirrors
            // Legacy table (old UI): ID, Authors, Title, Publisher, Year, Pages, Language, Size, Ext, Mirrors
            var usesNewLayout = cells.Count == 9;

            var titleCell = usesNewLayout ? cells[0] : cells[2];
            var title = ExtractTitleFromCell(titleCell);
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var authorsCell = usesNewLayout ? cells[1] : cells[1];
            var authorsText = HtmlEntity.DeEntitize(authorsCell.InnerText.Trim());
            var authors = ParseAuthors(authorsText);

            var publisherCell = usesNewLayout ? cells[2] : cells[3];
            var publisher = HtmlEntity.DeEntitize(publisherCell.InnerText.Trim());

            int? year = null;
            var yearCell = usesNewLayout ? cells[3] : cells[4];
            if (int.TryParse(yearCell.InnerText.Trim(), out var y) && y > 1000 && y < 3000)
                year = y;

            var languageCell = usesNewLayout ? cells[4] : cells[6];
            var language = HtmlEntity.DeEntitize(languageCell.InnerText.Trim());
            var fileSizeCell = usesNewLayout ? cells[6] : cells[7];
            var fileSize = fileSizeCell.InnerText.Trim().Replace(" ", "");
            var formatCell = usesNewLayout ? cells[7] : cells[8];
            var format = formatCell.InnerText.Trim().ToUpperInvariant();

            // Extract MD5 from mirror link
            var mirrorsCell = usesNewLayout ? cells[8] : cells[9];
            var mirrorLinks = mirrorsCell.SelectNodes(".//a");
            var md5 = "";

            if (mirrorLinks != null)
            {
                foreach (var link in mirrorLinks)
                {
                    var href = link.GetAttributeValue("href", "");
                    md5 = ExtractMd5FromUrl(href);
                    if (!string.IsNullOrEmpty(md5))
                        break;
                }
            }

            if (string.IsNullOrEmpty(md5))
                return null;

            var dto = new BookDto(
                title,
                md5,
                authors,
                language,
                format,
                "libgen",
                fileSize,
                "Non-fiction",
                publisher,
                year,
                null,
                null
            );

            // LibGen cover URLs
            dto.CoverCandidates.AddRange(new[]
            {
                $"https://libgen.is/covers/{md5[..^1]}/{md5}.jpg",
                $"https://libgen.rs/covers/{md5[..^1]}/{md5}.jpg"
            });

            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractTitleFromCell(HtmlNode titleCell)
    {
        var links = titleCell.SelectNodes(".//a");
        if (links != null)
        {
            foreach (var link in links)
            {
                var text = HtmlEntity.DeEntitize(link.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, "[A-Za-z]"))
                    return text;
            }
        }

        return HtmlEntity.DeEntitize(titleCell.InnerText).Trim();
    }

    private static List<string> ParseAuthors(string authorsText)
    {
        if (string.IsNullOrWhiteSpace(authorsText))
            return new List<string>();

        var normalized = authorsText
            .Replace(" & ", ";", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", ";", StringComparison.OrdinalIgnoreCase);

        return normalized
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();
    }

    private static string ExtractMd5FromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return "";

        // LibGen URLs contain MD5 in various formats:
        // /main/{MD5}
        // /book/index.php?md5={MD5}
        // library.lol/main/{MD5}

        var md5Match = Regex.Match(url, @"/main/([a-f0-9]{32})", RegexOptions.IgnoreCase);
        if (md5Match.Success)
            return md5Match.Groups[1].Value.ToLowerInvariant();

        md5Match = Regex.Match(url, @"md5=([a-f0-9]{32})", RegexOptions.IgnoreCase);
        if (md5Match.Success)
            return md5Match.Groups[1].Value.ToLowerInvariant();

        return "";
    }

    public async Task<string?> GetDownloadUrlAsync(string md5)
    {
        try
        {
            // LibGen download flow: go to /main/{md5} page and extract the actual download link
            var html = await GetStringWithFallbackAsync($"/main/{md5.ToUpperInvariant()}");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Look for download link - usually the first "GET" link
            var downloadLink = doc.DocumentNode
                .SelectSingleNode("//a[contains(text(), 'GET') or contains(@href, 'download')]");

            var url = downloadLink?.GetAttributeValue("href", null);

            if (!string.IsNullOrEmpty(url) && !url.StartsWith("http"))
            {
                // Relative URL - prepend base domain
                url = $"{BaseDomains[0]}{url}";
            }

            return url;
        }
        catch
        {
            return null;
        }
    }

    public async Task<HttpResponseMessage?> GetDownloadResponseAsync(
        string md5,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead)
    {
        var downloadUrl = await GetDownloadUrlAsync(md5);
        if (string.IsNullOrEmpty(downloadUrl))
            return null;

        try
        {
            var response = await _http.GetAsync(downloadUrl, completionOption);
            if (response.IsSuccessStatusCode)
                return response;

            response.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> GetStringWithFallbackAsync(string pathAndQuery)
    {
        using var resp = await GetWithFallbackAsync(pathAndQuery);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> GetWithFallbackAsync(string pathAndQuery)
    {
        Console.WriteLine($"[LibGen HTTP] Starting fallback request for: {pathAndQuery}");
        HttpResponseMessage? lastResponse = null;
        var domainIndex = 0;

        foreach (var domain in BaseDomains)
        {
            domainIndex++;
            var uri = new Uri($"{domain}{pathAndQuery}");
            Console.WriteLine($"[LibGen HTTP] Attempt {domainIndex}/{BaseDomains.Length} - Trying: {uri}");

            try
            {
                var resp = await _http.GetAsync(uri);
                Console.WriteLine($"[LibGen HTTP] ✓ Response received: {resp.StatusCode}");

                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[LibGen HTTP] ✓ Success! Using domain: {domain}");
                    return resp;
                }

                Console.WriteLine($"[LibGen HTTP] ✗ Non-success status {resp.StatusCode}, trying next domain...");
                lastResponse?.Dispose();
                lastResponse = resp;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Console.WriteLine($"[LibGen HTTP] ✗ Timeout for {domain} - trying next domain...");
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine($"[LibGen HTTP] ✗ Request cancelled for {domain} - trying next domain...");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[LibGen HTTP] ✗ HTTP error for {domain}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibGen HTTP] ✗ Unexpected error for {domain}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        if (lastResponse != null)
        {
            var status = (int)lastResponse.StatusCode;
            Console.WriteLine($"[LibGen HTTP] ✗ All {BaseDomains.Length} domains failed. Last status: {status}");
            lastResponse.Dispose();
            throw new HttpRequestException($"Request failed with status {status} for all LibGen domains");
        }

        Console.WriteLine($"[LibGen HTTP] ✗ All {BaseDomains.Length} domains failed with no response.");
        throw new HttpRequestException("Request failed for all LibGen domains - all timed out or threw exceptions");
    }
}
#nullable restore
