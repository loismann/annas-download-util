using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace AnnasArchive.Core.Services;

public class OpenLibraryService : IOpenLibraryService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan AuthorCacheDuration = TimeSpan.FromHours(6);

    public OpenLibraryService(IHttpClientFactory httpFactory, IMemoryCache cache)
    {
        _httpFactory = httpFactory;
        _cache = cache;
    }

    #region Book Description

    public async Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null)
    {
        var workKey = await FindWorkKeyAsync(title, author, isbn);
        if (workKey == null)
        {
            Console.WriteLine($"[OpenLibrary] Could not find work key for '{title}' by {author}");
            return null;
        }

        var description = await TryGetWorkDescriptionAsync(workKey);
        if (!string.IsNullOrWhiteSpace(description))
        {
            Console.WriteLine($"[OpenLibrary] Found description from Works API for '{title}'");
            return description;
        }

        var editionKey = await FindEditionKeyAsync(workKey);
        if (editionKey != null)
        {
            description = await TryGetEditionDescriptionAsync(editionKey);
            if (!string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine($"[OpenLibrary] Found description from Edition API for '{title}'");
                return description;
            }
        }

        description = await TryGetFirstSentenceAsync(title, author);
        if (!string.IsNullOrWhiteSpace(description))
        {
            Console.WriteLine($"[OpenLibrary] Found first_sentence from Search API for '{title}'");
            return description;
        }

        Console.WriteLine($"[OpenLibrary] No description found for '{title}' by {author}");
        return null;
    }

    #endregion

    #region Author Suggestions

    public async Task<List<OpenLibraryAuthorSuggestion>> GetAuthorSuggestionsAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<OpenLibraryAuthorSuggestion>();

        var cacheKey = $"openlib_authors_{title.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out List<OpenLibraryAuthorSuggestion>? cached) && cached != null)
            return cached;

        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            http.Timeout = TimeSpan.FromSeconds(3);

            var query = Uri.EscapeDataString(title.Trim());
            var url = $"https://openlibrary.org/search.json?title={query}&limit=10";
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new List<OpenLibraryAuthorSuggestion>();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                return new List<OpenLibraryAuthorSuggestion>();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in docs.EnumerateArray())
            {
                if (!item.TryGetProperty("author_name", out var authorNames) || authorNames.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var author in authorNames.EnumerateArray())
                {
                    var name = author.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var key = name.Trim();
                    counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
                }
            }

            if (counts.Count == 0)
                return new List<OpenLibraryAuthorSuggestion>();

            var max = counts.Values.Max();
            string ConfidenceFromScore(int score)
            {
                var ratio = score / (double)max;
                if (ratio >= 0.66) return "high";
                if (ratio >= 0.34) return "medium";
                return "low";
            }

            var results = counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(5)
                .Select(kv => new OpenLibraryAuthorSuggestion(kv.Key, ConfidenceFromScore(kv.Value)))
                .ToList();

            _cache.Set(cacheKey, results, AuthorCacheDuration);
            return results;
        }
        catch
        {
            return new List<OpenLibraryAuthorSuggestion>();
        }
    }

    #endregion

    #region Cover Images

    public async Task<string?> GetCoverUrlAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            http.Timeout = TimeSpan.FromSeconds(3);

            var candidateTitles = BuildCoverTitleCandidates(title);
            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
                var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=10";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    continue;

                int bestScore = -1;
                int bestCoverId = -1;

                foreach (var item in docs.EnumerateArray())
                {
                    if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                        continue;

                    var coverId = coverProp.GetInt32();
                    var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                        ? editionProp.GetInt32()
                        : 0;

                    if (editionCount > bestScore)
                    {
                        bestScore = editionCount;
                        bestCoverId = coverId;
                    }
                }

                if (bestCoverId > 0)
                    return $"https://covers.openlibrary.org/b/id/{bestCoverId}-L.jpg";
            }

            if (!string.IsNullOrWhiteSpace(author))
                return await GetCoverUrlAsync(title, null);
        }
        catch
        {
            return null;
        }

        return null;
    }

    public async Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<string>();

        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            http.Timeout = TimeSpan.FromSeconds(4);

            var coverScores = new Dictionary<int, int>();
            var candidateTitles = BuildCoverTitleCandidates(title);

            foreach (var candidate in candidateTitles)
            {
                var titleQuery = Uri.EscapeDataString(candidate);
                var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
                var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=20";

                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in docs.EnumerateArray())
                {
                    if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                        continue;

                    var coverId = coverProp.GetInt32();
                    var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                        ? editionProp.GetInt32()
                        : 0;

                    if (coverScores.TryGetValue(coverId, out var existing))
                    {
                        if (editionCount > existing)
                            coverScores[coverId] = editionCount;
                    }
                    else
                    {
                        coverScores[coverId] = editionCount;
                    }
                }
            }

            var covers = coverScores
                .OrderByDescending(kvp => kvp.Value)
                .Take(limit)
                .Select(kvp => $"https://covers.openlibrary.org/b/id/{kvp.Key}-L.jpg")
                .ToList();

            if (covers.Count == 0 && !string.IsNullOrWhiteSpace(author))
                return await GetCoverCandidatesAsync(title, null, limit);

            return covers;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> BuildCoverTitleCandidates(string title)
    {
        var candidates = new List<string> { title };

        // Try without subtitle
        var colonIndex = title.IndexOf(':');
        if (colonIndex > 0)
            candidates.Add(title.Substring(0, colonIndex).Trim());

        // Try without parenthetical
        var parenIndex = title.IndexOf('(');
        if (parenIndex > 0)
            candidates.Add(title.Substring(0, parenIndex).Trim());

        return candidates.Distinct().ToList();
    }

    #endregion

    #region Private Description Helpers

    private async Task<string?> FindWorkKeyAsync(string title, string author, string? isbn)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");

            if (!string.IsNullOrWhiteSpace(isbn))
            {
                var isbnUrl = $"https://openlibrary.org/isbn/{isbn}.json";
                var isbnResponse = await http.GetAsync(isbnUrl);
                if (isbnResponse.IsSuccessStatusCode)
                {
                    var isbnDoc = await isbnResponse.Content.ReadFromJsonAsync<JsonDocument>();
                    if (isbnDoc?.RootElement.TryGetProperty("works", out var works) == true &&
                        works.ValueKind == JsonValueKind.Array &&
                        works.GetArrayLength() > 0)
                    {
                        var firstWork = works[0];
                        if (firstWork.TryGetProperty("key", out var keyProp))
                            return keyProp.GetString();
                    }
                }
            }

            var searchTerms = $"{title} {author}".Trim();
            var searchQuery = Uri.EscapeDataString(searchTerms);
            var searchUrl = $"https://openlibrary.org/search.json?q={searchQuery}&fields=key&limit=1";

            var response = await http.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("docs", out var docs) == true &&
                docs.ValueKind == JsonValueKind.Array &&
                docs.GetArrayLength() > 0)
            {
                var firstDoc = docs[0];
                if (firstDoc.TryGetProperty("key", out var keyProp))
                    return keyProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error finding work key: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetWorkDescriptionAsync(string workKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{workKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("description", out var descProp) == true)
            {
                if (descProp.ValueKind == JsonValueKind.String)
                    return descProp.GetString();
                else if (descProp.ValueKind == JsonValueKind.Object &&
                         descProp.TryGetProperty("value", out var valueProp))
                    return valueProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting work description: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> FindEditionKeyAsync(string workKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{workKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("editions", out var editions) == true &&
                editions.ValueKind == JsonValueKind.Array &&
                editions.GetArrayLength() > 0)
            {
                var firstEdition = editions[0];
                if (firstEdition.TryGetProperty("key", out var keyProp))
                    return keyProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error finding edition key: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetEditionDescriptionAsync(string editionKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{editionKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("description", out var descProp) == true)
            {
                if (descProp.ValueKind == JsonValueKind.String)
                    return descProp.GetString();
                else if (descProp.ValueKind == JsonValueKind.Object &&
                         descProp.TryGetProperty("value", out var valueProp))
                    return valueProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting edition description: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetFirstSentenceAsync(string title, string author)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var searchTerms = $"{title} {author}".Trim();
            var searchQuery = Uri.EscapeDataString(searchTerms);
            var url = $"https://openlibrary.org/search.json?q={searchQuery}&fields=first_sentence&limit=1";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("docs", out var docs) == true &&
                docs.ValueKind == JsonValueKind.Array &&
                docs.GetArrayLength() > 0)
            {
                var firstDoc = docs[0];
                if (firstDoc.TryGetProperty("first_sentence", out var sentenceProp))
                {
                    if (sentenceProp.ValueKind == JsonValueKind.String)
                        return sentenceProp.GetString();
                    else if (sentenceProp.ValueKind == JsonValueKind.Array &&
                             sentenceProp.GetArrayLength() > 0)
                        return sentenceProp[0].GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting first sentence: {ex.Message}");
            return null;
        }
    }

    #endregion
}
