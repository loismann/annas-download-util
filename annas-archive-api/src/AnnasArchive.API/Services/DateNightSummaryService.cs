using System.Text.Json;
using AnnasArchive.API.Data;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Generates the flyer's "sensationalist" one-line pitch for a pool movie and caches
/// it permanently — per DOCS/DATE_NIGHT_FEATURE.md: "generated once per movie and
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

    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiResponseParser _responseParser;
    private readonly ITokenUsageService _tokenUsage;
    private readonly IModelSelectionService _modelSelection;
    private readonly IConfiguration _config;
    private readonly AppDatabase _db;
    private readonly SemaphoreSlim _generationLock = new(1, 1);

    public DateNightSummaryService(
        IHttpClientFactory httpFactory,
        IAiResponseParser responseParser,
        ITokenUsageService tokenUsage,
        IModelSelectionService modelSelection,
        IConfiguration config,
        AppDatabase db)
    {
        _httpFactory = httpFactory;
        _responseParser = responseParser;
        _tokenUsage = tokenUsage;
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
                Log.Warning("[DateNight] Summary generation failed for '{Title}': {Message}", title, ex.Message);
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
        using var http = _httpFactory.CreateClient("OpenAI");

        var userPrompt = $"Title: {title}\nYear: {year?.ToString() ?? "unknown"}\n" +
                          $"Plot: {(string.IsNullOrWhiteSpace(overview) ? "(no description available)" : overview)}";

        var payload = new
        {
            model = _modelSelection.GetModelFast(),
            input = $"{SystemPrompt}\n\n{userPrompt}",
            max_output_tokens = _config.GetValue<int>("AI:MaxCompletionTokens:DateNightSummary"),
            temperature = _config.GetValue<double>("AI:Temperature:DateNightSummary")
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[DateNight] OpenAI summary request failed ({StatusCode}): {Body}", response.StatusCode, errorBody);
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var text = _responseParser.ExtractText(doc.RootElement)?.Trim().Trim('"');

        if (doc.RootElement.TryGetProperty("usage", out var usage) && !string.IsNullOrWhiteSpace(attributedTo))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            _tokenUsage.AddUsage(attributedTo, promptTokens, completionTokens);
        }

        return text;
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
            Log.Warning("[DateNight] Summary cache unreadable, starting fresh: {Message}", ex.Message);
            return new();
        }
    }

    private void SaveCache(Dictionary<int, string> cache) =>
        _db.SetState(SummariesStateKey, JsonSerializer.Serialize(cache, JsonOptions));
}
