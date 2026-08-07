using System.Text.Json;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using Serilog;

namespace AnnasArchive.API.Services.BookDiscovery;

/// <summary>
/// Answers "who wrote this?" from OpenLibrary's search index, so the common case
/// never reaches OpenAI at all.
///
/// Confidence is derived from agreement rather than asserted: OpenLibrary
/// returns up to ten editions of a title, and an author credited on eight of
/// them is a better answer than one credited on one. The ratio to the top scorer
/// becomes high/medium/low.
/// </summary>
public static class OpenLibraryAuthorLookup
{
    // Keys are book titles typed by a person, so they must match
    // case-insensitively — otherwise "Dune" and "dune" are two entries and one
    // of them is a needless API call. Capacity comes from
    // Caching:AuthorSuggestionCacheSize via ConfigureCache; that setting existed,
    // was documented and had a default, but nothing ever read it, so this cache
    // previously grew without limit for the life of the process.
    private static LruCache<string, List<AuthorSuggestion>> _cache =
        new(capacity: 500, ttl: HttpTimeouts.AuthorCacheTtl, keyComparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the configured capacity. Mirrors LibraryEpubCache.ConfigureCache
    /// and is called from ServiceConfiguration.ConfigureCaches at startup.
    /// </summary>
    public static void ConfigureCache(int capacity)
    {
        if (capacity > 0)
        {
            _cache = new LruCache<string, List<AuthorSuggestion>>(
                capacity, HttpTimeouts.AuthorCacheTtl, StringComparer.OrdinalIgnoreCase);
            Log.Information("[AiBookSearch] Author suggestion cache configured with capacity {Capacity}", capacity);
        }
    }

    /// <summary>
    /// The likely authors of <paramref name="title"/>, best first, or an empty
    /// list if OpenLibrary has nothing or is unreachable. Never throws — an
    /// empty list is the caller's signal to ask the model instead.
    /// </summary>
    public static async Task<List<AuthorSuggestion>> SuggestAsync(string title, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];

        // The cache compares keys case-insensitively itself, and TTL and
        // eviction live in LruCache, so this only needs to trim.
        if (_cache.TryGetValue(title.Trim(), out var cached)) return cached;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = HttpTimeouts.OpenLibraryCacheLookup;

            var query = Uri.EscapeDataString(title.Trim());
            using var response = await http.GetAsync($"https://openlibrary.org/search.json?title={query}&limit=10");
            if (!response.IsSuccessStatusCode) return [];

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var counts = CountAuthorCredits(doc.RootElement);
            if (counts.Count == 0) return [];

            var results = RankByAgreement(counts);
            _cache.Set(title.Trim(), results);
            return results;
        }
        catch
        {
            // Deliberately silent and total: this is an optimisation in front of
            // the model, and every failure mode has the same answer.
            return [];
        }
    }

    private static Dictionary<string, int> CountAuthorCredits(JsonElement root)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            return counts;

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

        return counts;
    }

    private static List<AuthorSuggestion> RankByAgreement(Dictionary<string, int> counts)
    {
        var max = counts.Values.Max();

        string Confidence(int score) => (score / (double)max) switch
        {
            >= 0.66 => "high",
            >= 0.34 => "medium",
            _ => "low"
        };

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)   // ties resolve alphabetically, so the same query gives the same order
            .Take(5)
            .Select(kv => new AuthorSuggestion(kv.Key, Confidence(kv.Value)))
            .ToList();
    }
}
