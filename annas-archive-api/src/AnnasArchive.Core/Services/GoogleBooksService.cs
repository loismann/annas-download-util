using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.Core.Services;

public class GoogleBooksService : IGoogleBooksService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CoverCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan CoverNotFoundCacheDuration = TimeSpan.FromHours(4);

    public GoogleBooksService(IHttpClientFactory httpFactory, IMemoryCache cache)
    {
        _httpFactory = httpFactory;
        _cache = cache;
    }

    #region Book Description

    public async Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null)
    {
        try
        {
            var http = _httpFactory.CreateClient("GoogleBooks");

            // Build query - prefer ISBN if available, otherwise use simple freeform search
            // (freeform search is more forgiving and finds more results than intitle:/inauthor: operators)
            string query;
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                query = $"isbn:{Uri.EscapeDataString(isbn)}";
            }
            else
            {
                // Simple freeform query - more forgiving than structured field operators
                var searchTerms = $"{title} {author}".Trim();
                query = Uri.EscapeDataString(searchTerms);
            }

            var url = $"https://www.googleapis.com/books/v1/volumes?q={query}&maxResults=1";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[GoogleBooks] API request failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();

            // Check if we have items in the response
            if (doc?.RootElement.TryGetProperty("items", out var items) == true &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                // Get the first item
                var firstItem = items[0];

                if (firstItem.TryGetProperty("volumeInfo", out var volumeInfo) &&
                    volumeInfo.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    var desc = description.GetString();
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        Log.Information("[GoogleBooks] Found description for {Title} by {Author}", title, author);
                        return desc;
                    }
                }
            }

            Log.Information("[GoogleBooks] No description found for {Title} by {Author}", title, author);
            return null;
        }
        catch (ArgumentException ex)
        {
            Log.Warning("[GoogleBooks] Invalid argument: {ParamName}", ex.ParamName);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning("[GoogleBooks] Error: {ErrorMessage}", ex.Message);
            return null;
        }
    }

    #endregion

    #region Cover Images

    public async Task<string?> GetCoverUrlAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Check cache first (includes negative caching for "not found")
        var cacheKey = $"gbooks_cover_{title.Trim().ToLowerInvariant()}_{author?.Trim().ToLowerInvariant() ?? ""}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            Log.Debug("[GoogleBooks] Cover cache hit for {Title}", title);
            return cached; // May be null for negative cache
        }

        try
        {
            var http = _httpFactory.CreateClient("GoogleBooks");
            http.Timeout = TimeSpan.FromSeconds(3);

            var candidateTitles = BuildCoverTitleCandidates(title);
            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"+inauthor:{Uri.EscapeDataString(author.Trim())}";
                var url = $"https://www.googleapis.com/books/v1/volumes?q=intitle:{titleQuery}{authorQuery}&maxResults=3";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("volumeInfo", out var volumeInfo)) continue;
                    if (!volumeInfo.TryGetProperty("imageLinks", out var imageLinks)) continue;

                    string? urlValue = null;
                    if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                        urlValue = thumb.GetString();
                    else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                        urlValue = smallThumb.GetString();

                    if (string.IsNullOrWhiteSpace(urlValue)) continue;
                    var coverUrl = urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                    _cache.Set(cacheKey, coverUrl, CoverCacheDuration);
                    return coverUrl;
                }
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                var result = await GetCoverUrlAsync(title, null);
                // Don't double-cache - the recursive call already cached
                return result;
            }
        }
        catch
        {
            // Cache the failure to prevent repeated API calls
            _cache.Set(cacheKey, (string?)null, CoverNotFoundCacheDuration);
            return null;
        }

        // Not found - cache negative result
        _cache.Set(cacheKey, (string?)null, CoverNotFoundCacheDuration);
        return null;
    }

    public async Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<string>();

        try
        {
            var http = _httpFactory.CreateClient("GoogleBooks");
            http.Timeout = TimeSpan.FromSeconds(5);

            var query = string.IsNullOrWhiteSpace(author)
                ? $"intitle:{title}"
                : $"intitle:{title} inauthor:{author}";
            var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults={Math.Max(limit, 5)}";

            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new List<string>();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new List<string>();

            var results = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var info)) continue;
                if (!info.TryGetProperty("imageLinks", out var imageLinks)) continue;

                string? urlValue = null;
                if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                    urlValue = thumb.GetString();
                else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                    urlValue = smallThumb.GetString();

                if (string.IsNullOrWhiteSpace(urlValue)) continue;

                // Upgrade to larger image by modifying URL parameters
                urlValue = urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                urlValue = urlValue.Replace("&zoom=1", "&zoom=0", StringComparison.OrdinalIgnoreCase);
                urlValue = urlValue.Replace("?zoom=1", "?zoom=0", StringComparison.OrdinalIgnoreCase);
                urlValue = urlValue.Replace("&edge=curl", "", StringComparison.OrdinalIgnoreCase);

                results.Add(urlValue);
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> BuildCoverTitleCandidates(string title)
    {
        var candidates = new List<string>();
        var trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return candidates;

        string Simplify(string value)
        {
            var withoutBracket = Regex.Replace(value, @"\[[^\]]+\]", "").Trim();
            var withoutParens = Regex.Replace(withoutBracket, @"\([^)]+\)", "").Trim();
            var withoutSeries = Regex.Replace(withoutParens, @"\bbook\s+\d+\b", "", RegexOptions.IgnoreCase).Trim();
            var withoutDash = Regex.Replace(withoutSeries, @"\s*-\s*\d+\s*-\s*", " ").Trim();
            return Regex.Replace(withoutDash, @"\s{2,}", " ").Trim();
        }

        var baseTitle = Simplify(trimmed);
        candidates.Add(baseTitle);

        var colonSplit = baseTitle.Split(':')[0].Trim();
        if (!string.Equals(colonSplit, baseTitle, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(colonSplit))
            candidates.Add(colonSplit);

        if (!string.Equals(trimmed, baseTitle, StringComparison.OrdinalIgnoreCase))
            candidates.Add(trimmed);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    #endregion
}
