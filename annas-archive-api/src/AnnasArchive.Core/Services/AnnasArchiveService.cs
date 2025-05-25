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
    public AnnaArchiveService(HttpClient http) => _http = http;

    private static readonly Regex IsbnRx =
        new(@"ISBN(?:-1[03])?:?\s*([0-9Xx\-]{10,17})", RegexOptions.IgnoreCase);

    private static readonly Regex ImgRx =
        new(@"https://[^""']+/covers[0-9]*/[^""']+\.jpg", RegexOptions.IgnoreCase);

    public async Task<IEnumerable<BookDto>> SearchAsync(string query, int limit = 10)
    {
        var html = await _http.GetStringAsync(
            $"/search?index=&page=1&q={Uri.EscapeDataString(query)}&display=&sort=");

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var anchors = doc.DocumentNode
            .SelectNodes("//a[contains(@href,'/md5/')]")
            ?.Where(a => a.InnerText
                          .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(limit)
            .ToList() ?? new();

        var sem   = new SemaphoreSlim(4);
        var tasks = anchors.Select(async link =>
        {
            await sem.WaitAsync();
            try
            {
                var md5 = Path.GetFileName(link.GetAttributeValue("href", ""))
                               .ToLowerInvariant();

                var dto = BuildDtoFromAnchor(link, md5);

                var (isbn, cover) = await GetIsbnAndCoverAsync(md5);
                dto.Isbn = isbn;

                if (!string.IsNullOrEmpty(cover))
                    dto.CoverCandidates.Insert(0, cover);

                if (!string.IsNullOrEmpty(isbn))
                    dto.CoverCandidates.Add(
                        $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false");

                // keep only s3-proxy covers
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

        return await Task.WhenAll(tasks);
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

    private static BookDto BuildDtoFromAnchor(HtmlNode link, string md5)
    {
        var lines = link.InnerText
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToArray();

        var meta      = lines[0].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).ToArray();
        var language  = meta.ElementAtOrDefault(0) ?? "";
        var format    = meta.ElementAtOrDefault(1) ?? "";
        var source    = meta.ElementAtOrDefault(2) ?? "";
        var fileSize  = meta.ElementAtOrDefault(3) ?? "";
        var bookType  = meta.ElementAtOrDefault(4) ?? "";
        var title     = lines.ElementAtOrDefault(1) ?? "";

        var third     = lines.ElementAtOrDefault(2) ?? "";
        int? year     = int.TryParse(third, out var y) ? y : null;
        var publisher = year == null ? third : "";

        var fourth    = lines.ElementAtOrDefault(3) ?? "";
        if (year == null && int.TryParse(fourth, out y))
        {
            year   = y;
            fourth = third;
        }

        var authors = fourth.TrimEnd(',')
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(a => a.Trim())
                            .Where(a => a.Length > 0)
                            .ToList();

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
                + "&path_index=0"
                + "&domain_index=0";

        var doc = await _http.GetFromJsonAsync<JsonElement>(url);
        if (doc.ValueKind != JsonValueKind.Object)
            return new List<string>();

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
                if (!string.IsNullOrEmpty(s))
                    results.Add(s);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches the raw fast_download document containing both download_url and account_fast_download_info.
    /// </summary>
    public async Task<JsonElement> GetMemberDownloadDocumentAsync(string md5, string key)
    {
        var url = $"/dyn/api/fast_download.json"
                + $"?md5={Uri.EscapeDataString(md5)}"
                + $"&key={Uri.EscapeDataString(key)}"
                + "&path_index=0"
                + "&domain_index=0";

        var doc = await _http.GetFromJsonAsync<JsonElement>(url);
        if (doc.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Failed to fetch download document.");
        return doc;
    }
}

#nullable restore
