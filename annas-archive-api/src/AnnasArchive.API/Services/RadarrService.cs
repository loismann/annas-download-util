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
public class RadarrService : ArrServiceBase, IRadarrService
{
    public RadarrService(HttpClient http, IConfiguration configuration)
        : base(http, configuration, "Radarr", "includeMovie=true", "/data/Movies")
    {
    }

    public Task<JsonArray> LookupMoviesAsync(string term, CancellationToken ct = default) =>
        GetJsonArrayAsync($"/api/v3/movie/lookup?term={Uri.EscapeDataString(term)}", ct);

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

        var response = await Http.PostAsJsonAsync("/api/v3/movie", movie, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Radarr] Add movie failed ({StatusCode}): {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Radarr] Added movie '{Title}'", movie["title"]?.ToString());
        return JsonNode.Parse(body) as JsonObject ?? [];
    }

    /// <summary>Radarr's own "interactive search" — same one triggered by the
    /// magnifying-glass icon in Radarr's UI — so results already include the
    /// full rejection reasoning Radarr computed against the movie's profile.</summary>
    public Task<JsonArray> SearchReleasesAsync(int movieId, CancellationToken ct = default) =>
        GetJsonArrayAsync($"/api/v3/release?movieId={movieId}", ct);

    public Task<JsonArray> GetAllMoviesAsync(CancellationToken ct = default) =>
        GetJsonArrayAsync("/api/v3/movie", ct);

    public async Task DeleteMovieAsync(int movieId, CancellationToken ct = default)
    {
        // Deleting the movie record alone doesn't touch anything already
        // grabbed and sitting in the download client's queue — that torrent/nzb
        // job keeps running completely independently, orphaned, forever. Clear
        // it first so nothing keeps downloading/seeding after the movie itself
        // is gone.
        await RemoveQueueItemsForAsync("movieId", movieId, ct);

        var response = await Http.DeleteAsync($"/api/v3/movie/{movieId}?deleteFiles=true", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Radarr] Delete movie {MovieId} failed ({StatusCode}): {Body}", movieId, response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Radarr] Deleted movie {MovieId} and its files", movieId);
    }

}
