using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

public interface ISonarrService
{
    /// <summary>Raw lookup results from Sonarr (backed by TheTVDB) — each entry is the
    /// exact object Sonarr expects back, unmodified, when adding that series.</summary>
    Task<JsonArray> LookupSeriesAsync(string term, CancellationToken ct = default);

    /// <summary>Registers a series for monitoring/download. Takes the exact object
    /// returned by LookupSeriesAsync for the chosen result — Sonarr's add endpoint
    /// expects the full lookup object back, not a stripped-down subset.
    /// <paramref name="monitoredSeasons"/>, if given, restricts monitoring to just
    /// those season numbers instead of the whole series (season 0 = Specials).</summary>
    Task<JsonObject> AddSeriesAsync(JsonObject series, IReadOnlyCollection<int>? monitoredSeasons = null, CancellationToken ct = default);

    /// <summary>Every series already added in Sonarr, seasons included — used by the
    /// frontend to cross-reference search results against what's already requested,
    /// down to which specific seasons are monitored.</summary>
    Task<JsonArray> GetAllSeriesAsync(CancellationToken ct = default);

    /// <summary>Adds seasons to an *already-added* series (found via GetAllSeriesAsync)
    /// rather than adding it fresh — merges with whatever's already monitored (never
    /// un-monitors a season the caller didn't mention) and triggers a search only for
    /// the newly-monitored seasons, not the whole series again.</summary>
    Task<JsonObject> UpdateSeriesSeasonsAsync(int seriesId, IReadOnlyCollection<int> monitoredSeasons, CancellationToken ct = default);

    Task<JsonObject> GetQueueAsync(CancellationToken ct = default);

    /// <summary>Per-episode list for a series, including Sonarr's own
    /// tracked <c>hasFile</c> flag — the source of truth for "is this
    /// actually downloaded," not something Jellyfin needs to be asked.</summary>
    Task<JsonArray> GetEpisodesAsync(int seriesId, CancellationToken ct = default);

    /// <summary>Removes the whole series from Sonarr and deletes all its files.</summary>
    Task DeleteSeriesAsync(int seriesId, CancellationToken ct = default);

    /// <summary>Deletes just one season's files and un-monitors that season,
    /// leaving the rest of the series (and its record) untouched — Sonarr has
    /// no single endpoint for this, so it's done as delete-each-file-in-the-
    /// season, then un-monitor.</summary>
    Task DeleteSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Sonarr's REST v3 API. Root folder and quality profile
/// are resolved dynamically from whatever's already configured in Sonarr
/// itself (set up once via its own web UI — see deployment notes) rather
/// than hardcoded here, since profile IDs aren't stable across installs.
/// </summary>
public class SonarrService : ISonarrService
{
    private readonly HttpClient _http;

    public SonarrService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        var baseUrl = configuration["Sonarr:BaseUrl"];
        var apiKey = configuration["Sonarr:ApiKey"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _http.BaseAddress = new Uri(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Remove("X-Api-Key");
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
    }

    public async Task<JsonArray> LookupSeriesAsync(string term, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(term);
        var response = await _http.GetAsync($"/api/v3/series/lookup?term={encoded}", ct);
        response.EnsureSuccessStatusCode();

        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task<JsonObject> AddSeriesAsync(JsonObject series, IReadOnlyCollection<int>? monitoredSeasons = null, CancellationToken ct = default)
    {
        var (rootFolderPath, qualityProfileId) = await ResolveDefaultsAsync(ct);

        series["rootFolderPath"] = rootFolderPath;
        series["qualityProfileId"] = qualityProfileId;
        series["monitored"] = true;
        series["seasonFolder"] = true;

        // A null/empty selection means "everything" (unchanged default
        // behavior) — only override individual seasons' monitored flags
        // when the caller actually picked a subset.
        if (monitoredSeasons is { Count: > 0 } && series["seasons"] is JsonArray seasons)
        {
            foreach (var seasonNode in seasons)
            {
                if (seasonNode is JsonObject seasonObj && seasonObj["seasonNumber"] is JsonValue seasonNumberValue)
                {
                    var seasonNumber = seasonNumberValue.GetValue<int>();
                    seasonObj["monitored"] = JsonValue.Create(monitoredSeasons.Contains(seasonNumber));
                }
            }
        }

        series["addOptions"] = new JsonObject
        {
            ["searchForMissingEpisodes"] = true
        };

        var response = await _http.PostAsJsonAsync("/api/v3/series", series, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Sonarr] Add series failed ({StatusCode}): {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Sonarr] Added series '{Title}'", series["title"]?.ToString());
        return JsonNode.Parse(body) as JsonObject ?? [];
    }

    public async Task<JsonArray> GetAllSeriesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v3/series", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task<JsonObject> UpdateSeriesSeasonsAsync(int seriesId, IReadOnlyCollection<int> monitoredSeasons, CancellationToken ct = default)
    {
        var getResponse = await _http.GetAsync($"/api/v3/series/{seriesId}", ct);
        getResponse.EnsureSuccessStatusCode();
        var series = await getResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Sonarr series {seriesId} not found.");

        var newlyMonitored = new List<int>();
        if (series["seasons"] is JsonArray seasons)
        {
            foreach (var seasonNode in seasons)
            {
                if (seasonNode is not JsonObject seasonObj || seasonObj["seasonNumber"] is not JsonValue seasonNumberValue)
                    continue;

                var seasonNumber = seasonNumberValue.GetValue<int>();
                var wasMonitored = seasonObj["monitored"]?.GetValue<bool>() ?? false;
                var shouldBeMonitored = wasMonitored || monitoredSeasons.Contains(seasonNumber);

                if (shouldBeMonitored && !wasMonitored)
                    newlyMonitored.Add(seasonNumber);

                seasonObj["monitored"] = JsonValue.Create(shouldBeMonitored);
            }
        }

        var putResponse = await _http.PutAsJsonAsync($"/api/v3/series/{seriesId}", series, ct);
        var body = await putResponse.Content.ReadAsStringAsync(ct);
        if (!putResponse.IsSuccessStatusCode)
        {
            Log.Warning("[Sonarr] Update series seasons failed ({StatusCode}): {Body}", putResponse.StatusCode, body);
            putResponse.EnsureSuccessStatusCode();
        }

        // Adding a series triggers a search automatically via addOptions; updating an
        // existing one doesn't, so newly-monitored seasons need an explicit search
        // command each — otherwise they'd just sit there monitored but never grabbed.
        foreach (var seasonNumber in newlyMonitored)
        {
            var searchResponse = await _http.PostAsJsonAsync("/api/v3/command", new JsonObject
            {
                ["name"] = "SeasonSearch",
                ["seriesId"] = seriesId,
                ["seasonNumber"] = seasonNumber
            }, ct);
            if (!searchResponse.IsSuccessStatusCode)
            {
                Log.Warning("[Sonarr] SeasonSearch command failed for series {SeriesId} season {SeasonNumber}: {StatusCode}",
                    seriesId, seasonNumber, searchResponse.StatusCode);
            }
        }

        Log.Information("[Sonarr] Updated series {SeriesId}, newly monitored seasons: {Seasons}",
            seriesId, string.Join(",", newlyMonitored));
        return JsonNode.Parse(body) as JsonObject ?? [];
    }

    public async Task<JsonObject> GetQueueAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v3/queue?includeSeries=true", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonObject ?? [];
    }

    public async Task<JsonArray> GetEpisodesAsync(int seriesId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/v3/episode?seriesId={seriesId}", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task DeleteSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/v3/series/{seriesId}?deleteFiles=true", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Sonarr] Delete series {SeriesId} failed ({StatusCode}): {Body}", seriesId, response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Sonarr] Deleted series {SeriesId} and its files", seriesId);
    }

    public async Task DeleteSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default)
    {
        var episodeFilesResponse = await _http.GetAsync($"/api/v3/episodefile?seriesId={seriesId}", ct);
        episodeFilesResponse.EnsureSuccessStatusCode();
        var episodeFiles = await episodeFilesResponse.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];

        var deletedCount = 0;
        foreach (var fileNode in episodeFiles)
        {
            if (fileNode is not JsonObject fileObj || (int?)fileObj["seasonNumber"] != seasonNumber)
                continue;

            var fileId = (int?)fileObj["id"];
            if (fileId is null) continue;

            var deleteResponse = await _http.DeleteAsync($"/api/v3/episodefile/{fileId}", ct);
            if (deleteResponse.IsSuccessStatusCode)
            {
                deletedCount++;
            }
            else
            {
                Log.Warning("[Sonarr] Delete episode file {FileId} (series {SeriesId} season {SeasonNumber}) failed: {StatusCode}",
                    fileId, seriesId, seasonNumber, deleteResponse.StatusCode);
            }
        }

        // Un-monitor the season so Sonarr doesn't immediately try to
        // re-download what was just deleted.
        var getResponse = await _http.GetAsync($"/api/v3/series/{seriesId}", ct);
        getResponse.EnsureSuccessStatusCode();
        var series = await getResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Sonarr series {seriesId} not found.");

        if (series["seasons"] is JsonArray seasons)
        {
            foreach (var seasonNode in seasons)
            {
                if (seasonNode is JsonObject seasonObj && (int?)seasonObj["seasonNumber"] == seasonNumber)
                {
                    seasonObj["monitored"] = JsonValue.Create(false);
                }
            }
        }

        var putResponse = await _http.PutAsJsonAsync($"/api/v3/series/{seriesId}", series, ct);
        putResponse.EnsureSuccessStatusCode();

        Log.Information("[Sonarr] Deleted season {SeasonNumber} of series {SeriesId} ({Count} files) and un-monitored it",
            seasonNumber, seriesId, deletedCount);
    }

    private async Task<(string rootFolderPath, int qualityProfileId)> ResolveDefaultsAsync(CancellationToken ct)
    {
        var rootFoldersResp = await _http.GetAsync("/api/v3/rootfolder", ct);
        rootFoldersResp.EnsureSuccessStatusCode();
        var rootFolders = await rootFoldersResp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];
        var rootFolderPath = rootFolders.Count > 0 ? rootFolders[0]?["path"]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(rootFolderPath))
            throw new InvalidOperationException("Sonarr has no root folder configured — add one (e.g. /data/TV) in Sonarr's Media Management settings first.");

        var profilesResp = await _http.GetAsync("/api/v3/qualityprofile", ct);
        profilesResp.EnsureSuccessStatusCode();
        var profiles = await profilesResp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];
        if (profiles.Count == 0)
            throw new InvalidOperationException("Sonarr has no quality profile configured.");
        var qualityProfileId = (int)(profiles[0]?["id"] ?? 0);

        return (rootFolderPath, qualityProfileId);
    }
}
