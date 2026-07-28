using System.Text.Json.Nodes;
using AnnasArchive.Core.Exceptions;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>How a movie should be registered in Radarr.
///
/// The default (<see cref="MonitorAndSearch"/>) is what the search page has always
/// done: monitor it and immediately go find a release. <see cref="CatalogOnly"/>
/// exists for the Date Night pool, which registers 100+ movies purely as *records*
/// — posters, metadata, and an ID to hang votes off — without any of them
/// downloading. Unmonitored is the operative part: a monitored movie with no file
/// is something Radarr will keep trying to acquire on its own schedule, which is
/// exactly the runaway-disk-usage outcome the pool is designed to avoid.</summary>
public enum MovieAddMode
{
    MonitorAndSearch,
    CatalogOnly
}

public interface IRadarrService
{
    /// <summary>Raw lookup results from Radarr (backed by TMDB) — each entry is the
    /// exact object Radarr expects back, unmodified, when adding that movie.</summary>
    Task<JsonArray> LookupMoviesAsync(string term, CancellationToken ct = default);

    /// <summary>Registers a movie for monitoring/download. Takes the exact object
    /// returned by LookupMoviesAsync for the chosen result.</summary>
    Task<JsonObject> AddMovieAsync(
        JsonObject movie,
        MovieAddMode mode = MovieAddMode.MonitorAndSearch,
        IReadOnlyCollection<int>? tagIds = null,
        CancellationToken ct = default);

    /// <summary>Resolves a tag label to its Radarr tag ID, creating the tag if it
    /// doesn't exist yet. Radarr tags are the partitioning mechanism used to keep
    /// the Date Night pool separable from the regular library.</summary>
    Task<int> EnsureTagAsync(string label, CancellationToken ct = default);

    /// <summary>Adds/removes tags and flips monitoring on movies already in Radarr,
    /// via its bulk editor endpoint. Any argument left null is left untouched.</summary>
    Task EditMoviesAsync(
        IReadOnlyCollection<int> movieIds,
        bool? monitored = null,
        IReadOnlyCollection<int>? addTagIds = null,
        IReadOnlyCollection<int>? removeTagIds = null,
        CancellationToken ct = default);

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

    /// <summary>One movie, fetched fresh — used where a cached snapshot (e.g.
    /// the pool list) might be stale, such as reading the current
    /// <c>movieFile.id</c> right before deleting it.</summary>
    Task<JsonObject?> GetMovieAsync(int movieId, CancellationToken ct = default);

    /// <summary>Deletes one movie's file from disk without touching the movie
    /// record itself — unlike <see cref="DeleteMovieAsync"/>, which removes
    /// both. This is the Date Night "watched" cleanup: the movie stays a real,
    /// re-gettable Radarr entry, just with nothing on disk.</summary>
    Task DeleteMovieFileAsync(int movieFileId, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Radarr's REST v3 API — mirrors SonarrService's shape
/// and same reasoning for resolving root folder/quality profile dynamically
/// rather than hardcoding IDs that aren't stable across installs.
/// </summary>
public class RadarrService : ArrServiceBase, IRadarrService
{
    public RadarrService(HttpClient http, IConfiguration configuration)
        : base(http, configuration, "Radarr", "includeMovie=true", "/data/Movies",
               defaultProfileName: "HD Bluray + WEB")
    {
    }

    public Task<JsonArray> LookupMoviesAsync(string term, CancellationToken ct = default) =>
        GetJsonArrayAsync($"/api/v3/movie/lookup?term={Uri.EscapeDataString(term)}", ct);

    public async Task<JsonObject> AddMovieAsync(
        JsonObject movie,
        MovieAddMode mode = MovieAddMode.MonitorAndSearch,
        IReadOnlyCollection<int>? tagIds = null,
        CancellationToken ct = default)
    {
        var (rootFolderPath, qualityProfileId) = await ResolveDefaultsAsync(ct);
        var acquire = mode == MovieAddMode.MonitorAndSearch;

        movie["rootFolderPath"] = rootFolderPath;
        movie["qualityProfileId"] = qualityProfileId;
        movie["monitored"] = acquire;
        movie["minimumAvailability"] = "released";
        movie["addOptions"] = new JsonObject
        {
            ["searchForMovie"] = acquire
        };
        if (tagIds is { Count: > 0 })
            movie["tags"] = new JsonArray(tagIds.Select(id => (JsonNode)id).ToArray());

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

    public async Task<int> EnsureTagAsync(string label, CancellationToken ct = default)
    {
        // Radarr lowercases tag labels on create, so match case-insensitively —
        // otherwise "Date-Night-Pool" would never find the "date-night-pool" it
        // just created and would try to create it again on every call.
        var tags = await GetJsonArrayAsync("/api/v3/tag", ct);
        foreach (var tag in tags.OfType<JsonObject>())
        {
            if (string.Equals(tag["label"]?.ToString(), label, StringComparison.OrdinalIgnoreCase))
                return (int)(tag["id"] ?? 0);
        }

        var response = await Http.PostAsJsonAsync("/api/v3/tag", new JsonObject { ["label"] = label }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Radarr] Create tag '{Label}' failed ({StatusCode}): {Body}", label, response.StatusCode, body);
            throw new ExternalApiException("Radarr", ArrErrorParsing.ExtractMessage(body), response.StatusCode, isTransient: false);
        }

        var id = (int)((JsonNode.Parse(body) as JsonObject)?["id"] ?? 0);
        Log.Information("[Radarr] Created tag '{Label}' (id {Id})", label, id);
        return id;
    }

    public async Task EditMoviesAsync(
        IReadOnlyCollection<int> movieIds,
        bool? monitored = null,
        IReadOnlyCollection<int>? addTagIds = null,
        IReadOnlyCollection<int>? removeTagIds = null,
        CancellationToken ct = default)
    {
        if (movieIds.Count == 0) return;

        // /api/v3/movie/editor applies one change set to many movies in a single
        // call, and — unlike PUT /api/v3/movie/{id} — doesn't require reading and
        // round-tripping each movie's full object first. Its applyTags modes are
        // mutually exclusive per request, so add and remove are two separate calls.
        var ids = new JsonArray(movieIds.Select(id => (JsonNode)id).ToArray());

        if (monitored is bool m)
            await PostEditorAsync(new JsonObject { ["movieIds"] = ids.DeepClone(), ["monitored"] = m }, ct);

        if (addTagIds is { Count: > 0 })
            await PostEditorAsync(new JsonObject
            {
                ["movieIds"] = ids.DeepClone(),
                ["tags"] = new JsonArray(addTagIds.Select(id => (JsonNode)id).ToArray()),
                ["applyTags"] = "add"
            }, ct);

        if (removeTagIds is { Count: > 0 })
            await PostEditorAsync(new JsonObject
            {
                ["movieIds"] = ids.DeepClone(),
                ["tags"] = new JsonArray(removeTagIds.Select(id => (JsonNode)id).ToArray()),
                ["applyTags"] = "remove"
            }, ct);
    }

    private async Task PostEditorAsync(JsonObject payload, CancellationToken ct)
    {
        var response = await Http.PutAsJsonAsync("/api/v3/movie/editor", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Radarr] Movie editor request failed ({StatusCode}): {Body}", response.StatusCode, body);
            throw new ExternalApiException("Radarr", ArrErrorParsing.ExtractMessage(body), response.StatusCode, isTransient: false);
        }
    }

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

    public async Task<JsonObject?> GetMovieAsync(int movieId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"/api/v3/movie/{movieId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body) as JsonObject;
    }

    public async Task DeleteMovieFileAsync(int movieFileId, CancellationToken ct = default)
    {
        var response = await Http.DeleteAsync($"/api/v3/moviefile/{movieFileId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Warning("[Radarr] Delete movie file {MovieFileId} failed ({StatusCode}): {Body}", movieFileId, response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        Log.Information("[Radarr] Deleted movie file {MovieFileId}", movieFileId);
    }
}
