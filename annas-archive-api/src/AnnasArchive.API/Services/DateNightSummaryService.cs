using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.Core.Services;
using AnnasArchive.API.Services.Ai;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Generates the flyer's "sensationalist" one-line pitch for a pool movie and caches
/// it permanently — per DOCS/features/DATE_NIGHT.md: "generated once per movie and
/// cached... so re-showing the same movie daily doesn't re-bill OpenAI." The cache is
/// keyed by movie, not by week, since the same ~289-movie pool cycles through for
/// years — once a movie has a pitch, it never needs a new one.
///
/// Deliberately does not go through DescriptionFetcherService: that service is
/// book-specific (Google Books/OpenLibrary cascade). A movie's "real description"
/// before embellishment is simply Radarr's own TMDB overview, already on hand.
/// Also deliberately skips the per-user AI token-limit gate other AI endpoints use —
/// the cache bounds total spend to at most ~289 calls ever, not per request, so
/// blocking a page load on someone else's monthly quota would be the wrong trade.
/// </summary>
public class DateNightSummaryService
{
    private const string SummariesStateKey = "date-night:summaries";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SystemPrompt =
        "You write one-sentence movie-poster pitch copy in the breathless style of a " +
        "1950s B-movie trailer or drive-in marquee — lurid, exclamation-heavy, a little " +
        "campy. Given a title, year, and real plot description, write ONE sentence (25 " +
        "words max) that sells it that way. No quotation marks, no preamble, just the line.";

    private readonly IAiResponsesCompletion _ai;
    private readonly IModelSelectionService _modelSelection;
    private readonly IConfiguration _config;
    private readonly AppDatabase _db;
    private readonly SemaphoreSlim _generationLock = new(1, 1);

    public DateNightSummaryService(
        IAiResponsesCompletion ai,
        IModelSelectionService modelSelection,
        IConfiguration config,
        AppDatabase db)
    {
        _ai = ai;
        _modelSelection = modelSelection;
        _config = config;
        _db = db;
    }

    public async Task<string?> GetOrGenerateSummaryAsync(
        int movieId, string title, int? year, string? overview, string? attributedTo = null, CancellationToken ct = default)
    {
        var cache = LoadCache();
        if (cache.TryGetValue(movieId, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        await _generationLock.WaitAsync(ct);
        try
        {
            // Another background warmer may have filled it while this caller was
            // waiting. Re-read before spending another OpenAI request.
            cache = LoadCache();
            if (cache.TryGetValue(movieId, out existing) && !string.IsNullOrWhiteSpace(existing))
                return existing;

            string? generated;
            try
            {
                generated = await GenerateAsync(title, year, overview, attributedTo, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DateNight] Summary generation failed for '{Title}'", title);
                return null;
            }

            if (string.IsNullOrWhiteSpace(generated)) return null;

            cache[movieId] = generated;
            SaveCache(cache);
            return generated;
        }
        finally
        {
            _generationLock.Release();
        }
    }

    /// <summary>Read-only fast path used while rendering the flyer. Page reads
    /// never call OpenAI; issuance/reset guarantees all five selected movies have
    /// already passed through <see cref="GetOrGenerateSummaryAsync"/>.</summary>
    public string? GetCachedSummary(int movieId)
    {
        var cache = LoadCache();
        return cache.TryGetValue(movieId, out var summary) && !string.IsNullOrWhiteSpace(summary)
            ? summary
            : null;
    }

    public HashSet<int> GetCachedMovieIds() =>
        LoadCache()
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToHashSet();

    public async Task EnsureSummariesAsync(
        IEnumerable<(int MovieId, string Title, int? Year, string? Overview)> movies,
        CancellationToken ct = default)
    {
        foreach (var movie in movies)
        {
            ct.ThrowIfCancellationRequested();
            await GetOrGenerateSummaryAsync(
                movie.MovieId, movie.Title, movie.Year, movie.Overview, attributedTo: null, ct);
        }
    }

    private async Task<string?> GenerateAsync(string title, int? year, string? overview, string? attributedTo, CancellationToken ct)
    {
        var userPrompt = $"Title: {title}\nYear: {year?.ToString() ?? "unknown"}\n" +
                          $"Plot: {(string.IsNullOrWhiteSpace(overview) ? "(no description available)" : overview)}";

        // attributedTo is null for the background pre-generation pass, which
        // walks the whole catalogue — that spend belongs to the household, not
        // to whoever last opened the page.
        var outcome = await _ai.CompleteAsync(
            new AiResponsesCall(
                Endpoint: "date-night-summary",
                Model: _modelSelection.GetModelFast(),
                SystemPrompt: SystemPrompt,
                Input: userPrompt,
                MaxOutputTokens: _config.GetValue<int>("AI:MaxCompletionTokens:DateNightSummary"),
                Temperature: _config.GetValue<double>("AI:Temperature:DateNightSummary")),
            attributedTo ?? AiSpend.BackgroundAccount,
            ct);

        if (!outcome.Succeeded)
        {
            Log.Warning("[DateNight] OpenAI summary request failed: {Reason}", outcome.FailureMessage);
            return null;
        }

        return outcome.Text?.Trim().Trim('"');
    }

    private Dictionary<int, string> LoadCache()
    {
        var json = _db.GetState(SummariesStateKey);
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, string>>(json, JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[DateNight] Summary cache unreadable, starting fresh");
            return new();
        }
    }

    private void SaveCache(Dictionary<int, string> cache) =>
        _db.SetState(SummariesStateKey, JsonSerializer.Serialize(cache, JsonOptions));
}
