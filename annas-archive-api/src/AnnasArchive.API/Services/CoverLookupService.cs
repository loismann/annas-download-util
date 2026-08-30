using System.Text.RegularExpressions;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Service for fetching book covers from multiple sources with cascading fallback.
/// Consolidates the common pattern of trying multiple services for cover images.
/// </summary>
public class CoverLookupService : ICoverLookupService
{
    private readonly IOpenLibraryService _openLibraryService;
    private readonly IGoogleBooksService _googleBooksService;

    public CoverLookupService(
        IOpenLibraryService openLibraryService,
        IGoogleBooksService googleBooksService)
    {
        _openLibraryService = openLibraryService;
        _googleBooksService = googleBooksService;
    }

    public async Task<CoverLookupResult> GetCoverAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new CoverLookupResult(null, null);

        Log.Information("[CoverLookup] Searching for cover: '{Title}' by '{Author}'", title, author ?? "unknown");

        // Open Library first, and the comment this replaces is worth keeping in
        // mind: it said Open Library's search API "has been down" and put Anna's
        // Archive thumbnails in front of it for that reason. Measured on
        // 2026-08-20, Open Library answers this exact query in 0.39s, and Anna's
        // cannot answer at all — its HTML went behind DDoS-Guard on 2026-08-13.
        // The ladder had quietly inverted, and nobody re-measured it because a
        // cover that does not load looks like a book without a cover.
        //
        // Anna's rung is gone rather than demoted. It reaches the site through
        // Playwright, so it does not fail quickly — it spends up to thirty
        // seconds per book behind a single shared browser lock, which is what
        // turned "covers are missing" into "the page takes a minute". A rung
        // that cannot succeed and is expensive to try is worse than no rung.

        // 1. Open Library — free, no key, and the only one of the three
        //    currently answering.
        try
        {
            var coverUrl = await _openLibraryService.GetCoverUrlAsync(title, author);
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                Log.Information("[CoverLookup] Found cover from Open Library");
                return new CoverLookupResult(coverUrl, "Open Library");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CoverLookup] Open Library lookup failed");
        }

        // 2. Try Google Books as fallback
        try
        {
            var coverUrl = await _googleBooksService.GetCoverUrlAsync(title, author);
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                Log.Information("[CoverLookup] Found cover from Google Books");
                return new CoverLookupResult(coverUrl, "Google Books");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CoverLookup] Google Books lookup failed");
        }

        Log.Information("[CoverLookup] No cover found for '{Title}'", title);
        return new CoverLookupResult(null, null);
    }

    public async Task<List<string>> GetCoverCandidatesAsync(string title, string? author = null, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<string>();

        var allCovers = new List<string>();
        var halfLimit = limit / 2;

        // Get candidates from both services in parallel
        var openLibraryTask = SafeGetCandidates(() => _openLibraryService.GetCoverCandidatesAsync(title, author, halfLimit));
        var googleBooksTask = SafeGetCandidates(() => _googleBooksService.GetCoverCandidatesAsync(title, author, halfLimit));

        await Task.WhenAll(openLibraryTask, googleBooksTask);

        allCovers.AddRange(await openLibraryTask);
        allCovers.AddRange(await googleBooksTask);

        return allCovers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public List<string> BuildTitleCandidates(string title)
    {
        var candidates = new List<string>();
        var trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return candidates;

        // Start with the simplified title (remove brackets, parens, series info)
        string Simplify(string value)
        {
            // Remove square brackets and contents [...]
            var withoutBracket = Regex.Replace(value, @"\[[^\]]+\]", "").Trim();
            // Remove parentheticals (...)
            var withoutParens = Regex.Replace(withoutBracket, @"\([^)]+\)", "").Trim();
            // Remove "Book N" patterns
            var withoutSeries = Regex.Replace(withoutParens, @"\bbook\s+\d+\b", "", RegexOptions.IgnoreCase).Trim();
            // Remove dash-separated numbers pattern like "- 1 -"
            var withoutDash = Regex.Replace(withoutSeries, @"\s*-\s*\d+\s*-\s*", " ").Trim();
            // Collapse multiple spaces
            return Regex.Replace(withoutDash, @"\s{2,}", " ").Trim();
        }

        var baseTitle = Simplify(trimmed);
        candidates.Add(baseTitle);

        // Try without subtitle (split on colon)
        var colonSplit = baseTitle.Split(':')[0].Trim();
        if (!string.Equals(colonSplit, baseTitle, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(colonSplit))
        {
            candidates.Add(colonSplit);
        }

        // Try the original if different from simplified
        if (!string.Equals(trimmed, baseTitle, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(trimmed);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<string>> SafeGetCandidates(Func<Task<List<string>>> getCandidates)
    {
        try
        {
            return await getCandidates();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CoverLookup] Failed to get candidates");
            return new List<string>();
        }
    }
}
