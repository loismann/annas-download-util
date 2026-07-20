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
    private readonly AnnaArchiveService _annaArchiveService;

    public CoverLookupService(
        IOpenLibraryService openLibraryService,
        IGoogleBooksService googleBooksService,
        AnnaArchiveService annaArchiveService)
    {
        _openLibraryService = openLibraryService;
        _googleBooksService = googleBooksService;
        _annaArchiveService = annaArchiveService;
    }

    public async Task<CoverLookupResult> GetCoverAsync(string title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new CoverLookupResult(null, null);

        Log.Information("[CoverLookup] Searching for cover: '{Title}' by '{Author}'", title, author ?? "unknown");

        // Anna's Archive search (free thumbnail already in the listing HTML)
        // is the primary path now — Open Library's search API has been down
        // and Google Books' unauthenticated quota exhausted for a while, so
        // both of the below are effectively dead ends kept only in case
        // either recovers; they're tried after, not before, to avoid paying
        // their latency/failure cost on every single lookup.
        try
        {
            var coverUrl = await _annaArchiveService.GetCoverByTitleAuthorAsync(title, author);
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                Log.Information("[CoverLookup] Found cover from Anna's Archive");
                return new CoverLookupResult(coverUrl, "Anna's Archive");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[CoverLookup] Anna's Archive lookup failed: {Message}", ex.Message);
        }

        // 1. Try Open Library first (usually higher quality covers)
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
            Log.Warning("[CoverLookup] Open Library lookup failed: {Message}", ex.Message);
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
            Log.Warning("[CoverLookup] Google Books lookup failed: {Message}", ex.Message);
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
            Log.Warning("[CoverLookup] Failed to get candidates: {Message}", ex.Message);
            return new List<string>();
        }
    }
}
