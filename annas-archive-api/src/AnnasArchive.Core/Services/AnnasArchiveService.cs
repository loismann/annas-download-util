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

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private static readonly Regex ImgRx =
        new(@"https://[^""']+/covers[0-9]*/[^""']+\.jpg", RegexOptions.IgnoreCase);

    public async Task<IEnumerable<BookDto>> SearchAsync(string query, int limit = 50)
    {
        var collected = new List<HtmlNode>();   // parent containers for each book
        var page = 1;

        /* 1️⃣  keep fetching pages until we have >= limit books or no more pages */
        while (collected.Count < limit)
        {
            var html = await _http.GetStringAsync(
                $"/search?index=&page={page}&q={Uri.EscapeDataString(query)}&display=&sort=");
            page++;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Find the book containers
            // Each book is in a div with class "flex pt-3 pb-3 border-b"
            var bookContainers = doc.DocumentNode
                .SelectNodes("//div[contains(@class,'flex') and contains(@class,'pt-3') and contains(@class,'pb-3') and contains(@class,'border-b')]")?
                .ToList() ?? new();

            if (bookContainers.Count == 0) break;      // ran out of pages
            collected.AddRange(bookContainers);
        }

        /* 2️⃣  trim to the requested limit */
        collected = collected.Take(limit).ToList();

        /* 3️⃣  build DTOs in parallel */
        var sem   = new SemaphoreSlim(10);  // Increased from 4 to 10 for faster parallel processing
        var tasks = collected.Select(async container =>
        {
            await sem.WaitAsync();
            try
            {
                // Get MD5 from the cover link (first child <a> with /md5/)
                var coverLink = container.SelectSingleNode("./a[contains(@href,'/md5/')]");
                if (coverLink == null) return null;

                var md5 = Path.GetFileName(coverLink.GetAttributeValue("href", ""))
                            .ToLowerInvariant();

                var dto = BuildDtoFromAnchor(container, md5);

                var (isbn, cover) = await GetIsbnAndCoverAsync(md5);
                dto.Isbn = isbn;

                if (!string.IsNullOrEmpty(cover))
                    dto.CoverCandidates.Insert(0, cover);

                if (!string.IsNullOrEmpty(isbn))
                    dto.CoverCandidates.Add(
                        $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false");

                var filtered = dto.CoverCandidates
                    .Where(u => u.Contains("s3proxy.", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                dto.CoverCandidates.Clear();
                dto.CoverCandidates.AddRange(filtered);

                return dto;
            }
            finally { sem.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null)!;
    }


    private async Task<(string? isbn, string? cover)> GetIsbnAndCoverAsync(string md5)
    {
        var html = await _http.GetStringAsync($"/md5/{md5}");

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

        return (isbn, cover);
    }

    private static BookDto BuildDtoFromAnchor(HtmlNode container, string md5)
    {
        // New HTML structure:
        // <a class="line-clamp-[3] ... js-vim-focus">TITLE</a>
        // <a class="line-clamp-[2] ... text-sm" href="/search?q=AUTHORS">AUTHORS</a>
        // <a class="line-clamp-[2] ... text-sm" href="/search?q=PUBLISHER">PUBLISHER, SERIES, YEAR</a>
        // <div class="text-gray-800 dark:text-slate-400 ...">LANG [code] · FORMAT · SIZE · YEAR · TYPE · SOURCES</div>

        // Extract title
        var titleNode = container.SelectSingleNode(".//a[contains(@class,'js-vim-focus')]");
        var title = titleNode?.InnerText?.Trim() ?? $"Unknown Title ({md5})";

        // Extract authors (has user-edit icon)
        var authorNode = container.SelectSingleNode(".//a[contains(@class,'text-sm')]/span[contains(@class,'icon-[mdi--user-edit]')]/parent::a");
        var authorText = authorNode?.InnerText?.Trim() ?? "";
        var authors = string.IsNullOrEmpty(authorText)
            ? new List<string>()
            : authorText.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(a => a.Trim())
                       .Where(a => a.Length > 0)
                       .ToList();

        // Extract publisher/series/year (has company icon)
        var publisherNode = container.SelectSingleNode(".//a[contains(@class,'text-sm')]/span[contains(@class,'icon-[mdi--company]')]/parent::a");
        var publisherText = publisherNode?.InnerText?.Trim() ?? "";

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
        var metadataText = metadataNode?.InnerText?.Trim() ?? "";

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
            $"https://annas-archive.org/covers/{md5}.jpg"
        });

        return dto;
    }

    public async Task<List<string>> GetDownloadLinksAsync(string md5)
    {
        var links = await _http.GetFromJsonAsync<List<string>>(
            $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}");
        return links ?? new List<string>();
    }

    public async Task<List<string>> GetMemberDownloadLinksAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0&domain_index=0";

        var doc = await _http.GetFromJsonAsync<JsonElement>(url);
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

        var doc = await _http.GetFromJsonAsync<JsonElement>(url);
        if (doc.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Failed to fetch download document.");
        return doc;
    }
}
#nullable restore
