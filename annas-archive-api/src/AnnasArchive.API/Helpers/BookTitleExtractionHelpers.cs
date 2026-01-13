using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NReadability;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for extracting book titles from web pages.
/// </summary>
public static class BookTitleExtractionHelpers
{
    /// <summary>
    /// Extracts book titles from URLs found in a query string.
    /// </summary>
    public static async Task<List<string>> ExtractBookTitlesFromQueryAsync(
        string query,
        IHttpClientFactory httpFactory,
        CancellationToken cancellationToken)
    {
        var urls = ExtractUrls(query);
        if (urls.Count == 0)
            return new List<string>();

        var results = new List<string>();
        foreach (var url in urls)
        {
            var titles = await FetchBookTitlesFromUrlAsync(url, httpFactory, cancellationToken);
            results.AddRange(titles);
            if (results.Count >= 120)
                break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var title in results)
        {
            if (seen.Add(title))
                deduped.Add(title);
        }

        return deduped;
    }

    /// <summary>
    /// Extracts URLs from a query string.
    /// </summary>
    public static List<string> ExtractUrls(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<string>();

        var matches = Regex.Matches(query, @"https?://\S+");
        return matches.Select(m => m.Value.Trim().TrimEnd(')', ']', '}', '.', ',', ';', '"', '\''))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fetches book titles from a URL by parsing the HTML content.
    /// </summary>
    public static async Task<List<string>> FetchBookTitlesFromUrlAsync(
        string url,
        IHttpClientFactory httpFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(12);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var resp = await http.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new List<string>();

            var html = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
                return new List<string>();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var container = doc.DocumentNode.SelectSingleNode("//article")
                            ?? doc.DocumentNode.SelectSingleNode("//main")
                            ?? doc.DocumentNode;
            var contentRoot = SelectBestContentRoot(container, doc, html, url) ?? container;

            var titles = new List<string>();
            var listCandidates = contentRoot.SelectNodes(".//ol|.//ul");
            var listNode = listCandidates?
                .Where(node => !IsNavigationNode(node) && !HasNavigationAncestor(node))
                .Select(node => new { Node = node, Score = CountLikelyBookItems(node) })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Node.SelectNodes(".//li")?.Count ?? 0)
                .FirstOrDefault()?.Node;

            var listLikelyCount = listNode is null ? 0 : CountLikelyBookItems(listNode);
            var listItems = listLikelyCount > 0 ? listNode?.SelectNodes(".//li") : null;
            if (listItems != null && listLikelyCount > 0)
            {
                foreach (var item in listItems)
                {
                    if (HasNavigationAncestor(item)) continue;
                    var emphasized = item.SelectNodes(".//em|.//i|.//cite|.//strong");
                    if (emphasized != null && emphasized.Count > 0)
                    {
                        foreach (var node in emphasized)
                        {
                            if (HasNavigationAncestor(node)) continue;
                            var candidate = CleanBookTitle(node.InnerText);
                            if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                                titles.Add(candidate);
                        }
                    }
                    else
                    {
                        var candidate = CleanBookTitle(item.InnerText);
                        if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                            titles.Add(candidate);
                    }
                }
            }

            if (titles.Count == 0)
            {
                var headings = contentRoot.SelectNodes(".//h2|.//h3") ?? doc.DocumentNode.SelectNodes("//h2|//h3");
                if (headings != null)
                {
                    foreach (var node in headings)
                    {
                        if (HasNavigationAncestor(node)) continue;
                        var candidate = CleanBookTitle(node.InnerText);
                        if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                            titles.Add(candidate);
                    }
                }
            }

            if (titles.Count == 0)
            {
                var emphasis = contentRoot.SelectNodes(".//p//strong|.//p//em|.//p//i|.//p//cite");
                if (emphasis != null)
                {
                    foreach (var node in emphasis)
                    {
                        if (HasNavigationAncestor(node)) continue;
                        var candidate = CleanBookTitle(node.InnerText);
                        if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                            titles.Add(candidate);
                    }
                }
            }

            return titles;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Cleans a raw book title string.
    /// </summary>
    public static string CleanBookTitle(string raw)
    {
        var text = WebUtility.HtmlDecode(raw ?? string.Empty);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"^\d+[\.\)\-:\s]+", "");
        text = Regex.Replace(text, @"^[-•*]+\s*", "");
        var bySplit = Regex.Split(text, @"\s+by\s+", RegexOptions.IgnoreCase);
        text = bySplit.Length > 1 ? bySplit[0] : text;
        text = Regex.Split(text, @"\s+[—–-]\s+").FirstOrDefault() ?? text;
        text = text.Trim().Trim('"', '\'', '\u201C', '\u201D', '\u2018', '\u2019');
        return text;
    }

    /// <summary>
    /// Checks if a title string is reasonable (not too short/long, has letters).
    /// </summary>
    public static bool IsReasonableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;
        if (title.Length < 2 || title.Length > 120)
            return false;
        var letters = title.Count(char.IsLetter);
        return letters >= 2;
    }

    /// <summary>
    /// Checks if a title looks like navigation text rather than a book title.
    /// </summary>
    public static bool LooksLikeNavigation(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("related post") || lower.Contains("related articles"))
            return true;
        if (lower.Contains("listen to this article"))
            return true;
        if (lower.Contains("read more") || lower.Contains("comments"))
            return true;
        if (lower.Contains("subscribe") || lower.Contains("newsletter"))
            return true;
        if (lower.Contains("category") || lower.Contains("categories"))
            return true;
        if (lower.Contains("advertisement") || lower.Contains("sponsored"))
            return true;
        return false;
    }

    /// <summary>
    /// Selects the best content root node from the HTML document.
    /// </summary>
    public static HtmlNode? SelectBestContentRoot(HtmlNode container, HtmlDocument doc, string html, string url)
    {
        var candidates = container.SelectNodes(".//*[contains(@class,'entry-content') or contains(@class,'post-content') or contains(@class,'article-content') or contains(@class,'post-content-inner') or contains(@class,'post-content-column')]");
        if (candidates != null && candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(node => (node.InnerText ?? string.Empty).Length)
                .FirstOrDefault();
        }

        return ExtractReadableRoot(doc, html, url);
    }

    /// <summary>
    /// Checks if a node has a navigation ancestor.
    /// </summary>
    public static bool HasNavigationAncestor(HtmlNode node)
    {
        var current = node.ParentNode;
        while (current != null)
        {
            if (IsNavigationNode(current))
                return true;
            current = current.ParentNode;
        }

        return false;
    }

    /// <summary>
    /// Checks if a node is a navigation node.
    /// </summary>
    public static bool IsNavigationNode(HtmlNode node)
    {
        var name = node.Name.ToLowerInvariant();
        if (name is "nav" or "header" or "footer" or "aside" or "form")
            return true;

        var classId = $"{node.GetAttributeValue("class", "")} {node.GetAttributeValue("id", "")}".ToLowerInvariant();
        string[] tokens =
        {
            "nav", "menu", "footer", "header", "sidebar", "widget", "breadcrumb", "related", "share",
            "social", "subscribe", "newsletter", "category", "tag", "promo", "advert", "ads", "comment",
            "search", "pagination", "toolbar"
        };

        return tokens.Any(token => classId.Contains(token));
    }

    /// <summary>
    /// Counts likely book items in a list node.
    /// </summary>
    public static int CountLikelyBookItems(HtmlNode listNode)
    {
        var listItems = listNode.SelectNodes(".//li");
        if (listItems == null || listItems.Count == 0)
            return 0;

        var count = 0;
        foreach (var item in listItems)
        {
            if (HasNavigationAncestor(item))
                continue;

            var emphasized = item.SelectNodes(".//em|.//i|.//cite|.//strong");
            if (emphasized != null && emphasized.Count > 0)
            {
                foreach (var node in emphasized)
                {
                    if (HasNavigationAncestor(node)) continue;
                    var candidate = CleanBookTitle(node.InnerText);
                    if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                    {
                        count++;
                        break;
                    }
                }
            }
            else
            {
                var candidate = CleanBookTitle(item.InnerText);
                if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Extracts the readable root node from the HTML document using NReadability.
    /// </summary>
    public static HtmlNode? ExtractReadableRoot(HtmlDocument doc, string html, string url)
    {
        try
        {
            var nReadability = new NReadabilityWebTranscoder();
            var nReadabilityResult = nReadability.Transcode(new WebTranscodingInput(html));
            if (nReadabilityResult.ContentExtracted)
            {
                var readableDoc = new HtmlDocument();
                var readableHtml = nReadabilityResult.ExtractedContent ?? string.Empty;
                readableDoc.LoadHtml(readableHtml);
                var readableRoot = readableDoc.DocumentNode.SelectSingleNode("//article")
                                   ?? readableDoc.DocumentNode.SelectSingleNode("//main")
                                   ?? readableDoc.DocumentNode;
                if (readableRoot != null)
                    return readableRoot;
            }

            var candidates = doc.DocumentNode.SelectNodes("//article|//main|//section|//div");
            if (candidates == null || candidates.Count == 0)
                return null;

            HtmlNode? best = null;
            double bestScore = 0;

            foreach (var node in candidates)
            {
                if (IsBoilerplateNode(node))
                    continue;

                var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty);
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length < 200)
                    continue;

                var linkTextLength = node.SelectNodes(".//a")?
                    .Select(a => HtmlEntity.DeEntitize(a.InnerText ?? string.Empty).Trim())
                    .Where(s => s.Length > 0)
                    .Sum(s => s.Length) ?? 0;

                var linkDensity = text.Length == 0 ? 1.0 : (double)linkTextLength / text.Length;
                var score = text.Length * (1 - linkDensity);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = node;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a node is likely boilerplate content.
    /// </summary>
    public static bool IsBoilerplateNode(HtmlNode node)
    {
        var classId = $"{node.GetAttributeValue("class", "")} {node.GetAttributeValue("id", "")}".ToLowerInvariant();
        if (classId.Contains("nav") || classId.Contains("menu") || classId.Contains("footer") || classId.Contains("header"))
            return true;
        if (classId.Contains("sidebar") || classId.Contains("widget") || classId.Contains("related"))
            return true;
        if (classId.Contains("promo") || classId.Contains("advert") || classId.Contains("ad-") || classId.Contains("ads"))
            return true;
        if (classId.Contains("newsletter") || classId.Contains("subscribe") || classId.Contains("share"))
            return true;
        if (classId.Contains("comment") || classId.Contains("breadcrumb"))
            return true;
        return false;
    }
}
