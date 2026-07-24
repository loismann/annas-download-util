using System.Text.Json.Nodes;
using AnnasArchive.Core.Exceptions;
using Serilog;

namespace AnnasArchive.API.Services;

public interface IRadarrService
{
    /// <summary>Raw lookup results from Radarr (backed by TMDB) — each entry is the
    /// exact object Radarr expects back, unmodified, when adding that movie.</summary>
    Task<JsonArray> LookupMoviesAsync(string term, CancellationToken ct = default);

    /// <summary>Registers a movie for monitoring/download. Takes the exact object
    /// returned by LookupMoviesAsync for the chosen result.</summary>
    Task<JsonObject> AddMovieAsync(JsonObject movie, CancellationToken ct = default);

    Task<JsonObject> GetQueueAsync(CancellationToken ct = default);

    /// <summary>Every release Radarr's indexers found for this movie, including
    /// ones its own quality profile would auto-reject (e.g. too large under a
    /// size-capped profile) — each entry carries a "rejections" list explaining
    /// why, so the caller can make an informed judgment call Radarr wouldn't
    /// make on its own.</summary>
    Task<JsonArray> SearchReleasesAsync(int movieId, CancellationToken ct = default);

    /// <summary>Force-grabs one specific release regardless of the quality
    /// profile's normal rejections — takes the exact object returned by
    /// SearchReleasesAsync for the chosen release, unmodified, since Radarr's
    /// grab endpoint expects the full release object back (same idiom as
    /// AddMovieAsync).</summary>
    Task GrabReleaseAsync(JsonObject release, CancellationToken ct = default);

    /// <summary>Every movie already added in Radarr, each with its own
    /// <c>hasFile</c>/<c>tmdbId</c> — used both for the search page's
    /// already-added cross-reference and the video library's browse list.</summary>
    Task<JsonArray> GetAllMoviesAsync(CancellationToken ct = default);

    /// <summary>Removes the movie from Radarr entirely and deletes its file
    /// from disk — a movie has no smaller unit to scope a delete to, unlike
    /// a TV series' seasons. First cancels any in-progress queue item for
    /// this movie (telling the download client to remove the torrent/nzb job
    /// and delete its data too, both completed and still-downloading), so
    /// nothing orphaned keeps running after the movie itself is gone.</summary>
    Task DeleteMovieAsync(int movieId, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Radarr's REST v3 API — mirrors SonarrService's shape
/// and same reasoning for resolving root folder/quality profile dynamically
/// rather than hardcoding IDs that aren't stable across installs.
/// </summary>
public class RadarrService : IRadarrService
{
    private readonly HttpClient _http;

    public RadarrService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        var baseUrl = configuration["Radarr:BaseUrl"];
        var apiKey = configuration["Radarr:ApiKey"];
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

    public async Task<JsonArray> LookupMoviesAsync(string term, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(term);
        var response = await _http.GetAsync($"/api/v3/movie/lookup?term={encoded}", ct);
        response.EnsureSuccessStatusCode();

        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task<JsonObject> AddMovieAsync(JsonObject movie, CancellationToken ct = default)
    {
        var (rootFolderPath, qualityProfileId) = await ResolveDefaultsAsync(ct);

        movie["rootFolderPath"] = rootFolderPath;
        movie["qualityProfileId"] = qualityProfileId;
        movie["monitored"] = true;
        movie["minimumAvailability"] = "released";
        movie["addOptions"] = new JsonObject
        {
            ["searchForMovie"] = true
        };

        var response = await _http.PostAsJsonAsync("/api/v3/movie", movie, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Radarr] Add movie failed ({StatusCode}): {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Radarr] Added movie '{Title}'", movie["title"]?.ToString());
        return JsonNode.Parse(body) as JsonObject ?? [];
    }

    public async Task<JsonObject> GetQueueAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v3/queue?includeMovie=true", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonObject ?? [];
    }

    public async Task<JsonArray> SearchReleasesAsync(int movieId, CancellationToken ct = default)
    {
        // This is Radarr's own "interactive search" — same one triggered by the
        // magnifying-glass icon in Radarr's UI — so results already include the
        // full rejection reasoning Radarr computed against the movie's profile.
        var response = await _http.GetAsync($"/api/v3/release?movieId={movieId}", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task GrabReleaseAsync(JsonObject release, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/v3/release", release, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Radarr] Grab release failed ({StatusCode}): {Body}", response.StatusCode, body);
            throw new ExternalApiException("Radarr", ArrErrorParsing.ExtractMessage(body), response.StatusCode, isTransient: false);
        }

        Log.Information("[Radarr] Manually grabbed release '{Title}'", release["title"]?.ToString());
    }

    public async Task<JsonArray> GetAllMoviesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/v3/movie", ct);
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return node as JsonArray ?? [];
    }

    public async Task DeleteMovieAsync(int movieId, CancellationToken ct = default)
    {
        // Deleting the movie record alone doesn't touch anything already
        // grabbed and sitting in the download client's queue — that torrent/nzb
        // job keeps running completely independently, orphaned, forever. Clear
        // it first so nothing keeps downloading/seeding after the movie itself
        // is gone.
        await RemoveQueueItemsForAsync(movieId, ct);

        var response = await _http.DeleteAsync($"/api/v3/movie/{movieId}?deleteFiles=true", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Radarr] Delete movie {MovieId} failed ({StatusCode}): {Body}", movieId, response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Radarr] Deleted movie {MovieId} and its files", movieId);
    }

    /// <summary>Finds every queue entry for this movie and removes it with
    /// removeFromClient=true — this tells qBittorrent/SABnzbd to actually
    /// cancel the job and delete its data (both the incomplete/temp location
    /// and, if already finished, the completed-downloads location), not just
    /// clear Radarr's own view of the queue. Best-effort: a queue item that
    /// fails to remove (e.g. a race where it finished importing in the
    /// meantime) is logged, not thrown — it shouldn't block the movie delete
    /// that's about to happen anyway.</summary>
    private async Task RemoveQueueItemsForAsync(int movieId, CancellationToken ct)
    {
        try
        {
            var queue = await GetQueueAsync(ct);
            var records = queue["records"] as JsonArray ?? [];
            foreach (var record in records)
            {
                if (record is not JsonObject obj) continue;
                if ((int?)obj["movieId"] != movieId) continue;

                var queueId = (int?)obj["id"];
                if (queueId is null) continue;

                var deleteResponse = await _http.DeleteAsync(
                    $"/api/v3/queue/{queueId}?removeFromClient=true&blocklist=false", ct);
                if (!deleteResponse.IsSuccessStatusCode)
                {
                    Log.Warning("[Radarr] Remove queue item {QueueId} for movie {MovieId} failed: {StatusCode}",
                        queueId, movieId, deleteResponse.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[Radarr] Failed to clear queue items for movie {MovieId}: {Message}", movieId, ex.Message);
        }
    }

    private async Task<(string rootFolderPath, int qualityProfileId)> ResolveDefaultsAsync(CancellationToken ct)
    {
        var rootFoldersResp = await _http.GetAsync("/api/v3/rootfolder", ct);
        rootFoldersResp.EnsureSuccessStatusCode();
        var rootFolders = await rootFoldersResp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];
        var rootFolderPath = rootFolders.Count > 0 ? rootFolders[0]?["path"]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(rootFolderPath))
            throw new InvalidOperationException("Radarr has no root folder configured — add one (e.g. /data/Movies) in Radarr's Media Management settings first.");

        var profilesResp = await _http.GetAsync("/api/v3/qualityprofile", ct);
        profilesResp.EnsureSuccessStatusCode();
        var profiles = await profilesResp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];
        if (profiles.Count == 0)
            throw new InvalidOperationException("Radarr has no quality profile configured.");
        var qualityProfileId = (int)(profiles[0]?["id"] ?? 0);

        return (rootFolderPath, qualityProfileId);
    }
}
