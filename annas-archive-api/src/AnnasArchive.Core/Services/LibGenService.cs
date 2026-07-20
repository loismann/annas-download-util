#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using HtmlAgilityPack;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Telemetry;
using Serilog;

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
        Log.Information("[LibGen] SearchAsync called with query={Query}, limit={Limit}, exact={Exact}", query, limit, exact);

        if (limit <= 0)
        {
            Log.Warning("[LibGen] Invalid limit: {Limit}", limit);
            return Enumerable.Empty<BookDto>();
        }

        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            Log.Information("[LibGen] Empty query after trim");
            return Enumerable.Empty<BookDto>();
        }

        Log.Information("[LibGen] Trying general search first...");
        // Try general search first (more reliable), then fall back to fiction search
        var generalResults = await SearchGeneralAsync(trimmedQuery, limit, exact);
        if (generalResults.Any())
        {
            Log.Information("[LibGen] General search returned {ResultCount} results", generalResults.Count());
            return generalResults;
        }

        Log.Information("[LibGen] General search returned no results, trying fiction search...");
        var fictionResults = await SearchFictionAsync(trimmedQuery, limit, exact);
        Log.Information("[LibGen] Fiction search returned {ResultCount} results", fictionResults.Count());
        return fictionResults;
    }

    private async Task<IEnumerable<BookDto>> SearchFictionAsync(string query, int limit, bool exact)
    {
        try
        {
            var searchQuery = exact ? $"\"{query}\"" : query;
            var url = $"/fiction/?q={Uri.EscapeDataString(searchQuery)}&criteria=&language=&format=";
            Log.Information("[LibGen Fiction] Fetching: {Url}", url);

            var html = await GetStringWithFallbackAsync(url);
            Log.Information("[LibGen Fiction] Received HTML response, length: {Length}", html.Length);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // LibGen fiction table structure: table with tbody containing book rows
            var rows = doc.DocumentNode
                .SelectNodes("//table[contains(@class, 'catalog')]//tbody/tr")?
                .Take(limit)
                .ToList() ?? new List<HtmlNode>();

            Log.Information("[LibGen Fiction] Found {RowCount} table rows", rows.Count);

            if (rows.Count == 0)
            {
                Log.Information("[LibGen Fiction] No rows found, trying alternative selector...");
                // Try alternative selectors
                var altRows = doc.DocumentNode
                    .SelectNodes("//table//tbody/tr")?
                    .Take(limit)
                    .ToList() ?? new List<HtmlNode>();
                Log.Information("[LibGen Fiction] Alternative selector found {RowCount} rows", altRows.Count);

                if (altRows.Count == 0)
                {
                    // Log HTML snippet for debugging
                    var snippet = html.Length > 500 ? html.Substring(0, 500) : html;
                    Log.Debug("[LibGen Fiction] HTML snippet: {Snippet}...", snippet);
                }

                return Enumerable.Empty<BookDto>();
            }

            var books = new List<BookDto>();
            foreach (var row in rows)
            {
                var book = ParseFictionRow(row);
                if (book != null)
                {
                    Log.Information("[LibGen Fiction] Parsed book: {Title}", book.Title);
                    books.Add(book);
                }
                else
                {
                    Log.Warning("[LibGen Fiction] Failed to parse row");
                }
            }

            Log.Information("[LibGen Fiction] Returning {BookCount} books", books.Count);
            return books;
        }
        catch (ArgumentException ex)
        {
            Log.Warning("[LibGen Fiction] Invalid argument: {ParamName}", ex.ParamName);
            return Enumerable.Empty<BookDto>();
        }
        catch (Exception ex)
        {
            Log.Warning("[LibGen Fiction] ERROR: {ErrorMessage}", ex.Message);
            Log.Debug("[LibGen Fiction] Stack trace: {StackTrace}", ex.StackTrace);
            return Enumerable.Empty<BookDto>();
        }
    }

    private async Task<IEnumerable<BookDto>> SearchGeneralAsync(string query, int limit, bool exact)
    {
        try
        {
            var searchQuery = exact ? $"\"{query}\"" : query;
            var url = $"/index.php?req={Uri.EscapeDataString(searchQuery)}&lg_topic=libgen&open=0&view=simple&res=25&phrase=1&column=def";
            Log.Information("[LibGen General] Fetching: {Url}", url);

            var html = await GetStringWithFallbackAsync(url);
            Log.Information("[LibGen General] Received HTML response, length: {Length}", html.Length);

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

            Log.Information("[LibGen General] Found {RowCount} table rows", rowList.Count);

            if (rowList.Count == 0)
            {
                Log.Information("[LibGen General] No rows found, trying alternative selector...");
                // Try alternative selectors
                var altRows = doc.DocumentNode
                    .SelectNodes("//table[@rules='rows']//tr[position()>1]")?
                    .Take(limit)
                    .ToList() ?? new List<HtmlNode>();
                Log.Information("[LibGen General] Alternative selector found {RowCount} rows", altRows.Count);

                if (altRows.Count == 0)
                {
                    // Log HTML snippet for debugging
                    var snippet = html.Length > 500 ? html.Substring(0, 500) : html;
                    Log.Debug("[LibGen General] HTML snippet: {Snippet}...", snippet);
                }

                return Enumerable.Empty<BookDto>();
            }

            var books = new List<BookDto>();
            foreach (var row in rowList)
            {
                var book = ParseGeneralRow(row);
                if (book != null)
                {
                    Log.Information("[LibGen General] Parsed book: {Title}", book.Title);
                    books.Add(book);
                }
                else
                {
                    Log.Warning("[LibGen General] Failed to parse row");
                }
            }

            Log.Information("[LibGen General] Returning {BookCount} books", books.Count);
            return books;
        }
        catch (ArgumentException ex)
        {
            Log.Warning("[LibGen General] Invalid argument: {ParamName}", ex.ParamName);
            return Enumerable.Empty<BookDto>();
        }
        catch (Exception ex)
        {
            Log.Warning("[LibGen General] ERROR: {ErrorMessage}", ex.Message);
            Log.Debug("[LibGen General] Stack trace: {StackTrace}", ex.StackTrace);
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
                Log.Warning("[LibGen Fiction Parse] Row has {CellCount} cells, need at least 9", cells?.Count ?? 0);
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

            // LibGen cover URLs — derived from the same BaseDomains used for
            // search, so this stays in sync if the mirrors rotate again
            // instead of drifting to a separately-hardcoded (and eventually
            // dead) pair of domains.
            dto.CoverCandidates.AddRange(
                BaseDomains.Select(d => $"{d}/fictioncovers/{md5[0]}/{md5}.jpg"));

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

            // LibGen cover URLs — same rationale as the fiction path above.
            dto.CoverCandidates.AddRange(
                BaseDomains.Select(d => $"{d}/covers/{md5[..^1]}/{md5}.jpg"));

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
        Log.Information("[LibGen HTTP] Starting fallback request for: {PathAndQuery}", pathAndQuery);
        HttpResponseMessage? lastResponse = null;
        var domainIndex = 0;
        var fallbackSw = Stopwatch.StartNew();

        foreach (var domain in BaseDomains)
        {
            domainIndex++;
            var uri = new Uri($"{domain}{pathAndQuery}");
            var domainSw = Stopwatch.StartNew();
            Log.Information("[LibGen HTTP] Attempt {AttemptNumber}/{TotalDomains} - Trying: {Uri}", domainIndex, BaseDomains.Length, uri);

            try
            {
                var resp = await _http.GetAsync(uri);
                Log.Information("[LibGen HTTP] Response received: {StatusCode}", resp.StatusCode);

                if (resp.IsSuccessStatusCode)
                {
                    Log.Information("[LibGen HTTP] Success! Using domain: {Domain}", domain);
                    PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, true, ("Domain", domain));
                    PerfLog.Record("LibGen.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, true, ("WinningDomain", domain));
                    return resp;
                }

                Log.Warning("[LibGen HTTP] Non-success status {StatusCode}, trying next domain...", resp.StatusCode);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("StatusCode", (int)resp.StatusCode));
                lastResponse?.Dispose();
                lastResponse = resp;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Log.Warning("[LibGen HTTP] Timeout for {Domain} - trying next domain...", domain);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Reason", "timeout"));
            }
            catch (TaskCanceledException)
            {
                Log.Warning("[LibGen HTTP] Request cancelled for {Domain} - trying next domain...", domain);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Reason", "cancelled"));
            }
            catch (HttpRequestException ex)
            {
                Log.Warning("[LibGen HTTP] HTTP error for {Domain}: {ErrorMessage}", domain, ex.Message);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Error", ex.Message));
            }
            catch (ArgumentException ex)
            {
                Log.Warning("[LibGen HTTP] Invalid argument for {Domain}: {ParamName}", domain, ex.ParamName);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Error", ex.ParamName));
            }
            catch (Exception ex)
            {
                Log.Warning("[LibGen HTTP] Unexpected error for {Domain}: {ExceptionType} - {ErrorMessage}", domain, ex.GetType().Name, ex.Message);
                PerfLog.Record("LibGen.DomainFetch", domainSw.Elapsed.TotalMilliseconds, false, ("Domain", domain), ("Error", ex.Message));
            }
        }

        PerfLog.Record("LibGen.DomainFallback", fallbackSw.Elapsed.TotalMilliseconds, false, ("Reason", "all domains failed"));

        if (lastResponse != null)
        {
            var status = (int)lastResponse.StatusCode;
            Log.Warning("[LibGen HTTP] All {DomainCount} domains failed. Last status: {Status}", BaseDomains.Length, status);
            lastResponse.Dispose();
            throw new HttpRequestException($"Request failed with status {status} for all LibGen domains");
        }

        Log.Warning("[LibGen HTTP] All {DomainCount} domains failed with no response.", BaseDomains.Length);
        throw new HttpRequestException("Request failed for all LibGen domains - all timed out or threw exceptions");
    }
}
#nullable restore
