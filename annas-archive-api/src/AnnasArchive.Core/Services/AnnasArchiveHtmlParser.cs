#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using AnnasArchive.Core.Models;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Turns Anna's Archive search-result HTML into <see cref="BookDto"/>s.
/// </summary>
/// <remarks>
/// Split out of <see cref="AnnasArchiveService"/>: this is pure parsing with no
/// HTTP, cache or Playwright dependency, and it was the single largest block in
/// that class. Keeping it separate means the scraping rules — which change
/// whenever Anna's Archive reshuffles its markup — can be read and tested
/// without the transport code around them.
///
/// Covered end-to-end by AnnasArchiveServiceHtmlParsingTests, which drives
/// SearchAsync against canned HTML.
/// </remarks>
public static class AnnasArchiveHtmlParser
{
    public static string BuildSearchQuery(string query, bool exact)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        if (exact)
            return $"\"{trimmed}\"";

        return trimmed;
    }

    public static BookDto BuildDtoFromAnchor(HtmlNode container, string md5)
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
}
