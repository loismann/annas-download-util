using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>Just enough of Jellyfin's file-download response for the endpoint
/// layer to relay it back to the browser as a real download (Content-Type,
/// a filename for Content-Disposition, and length for a progress bar).</summary>
public record JellyfinDownloadResult(Stream Body, string ContentType, string? FileName, long? ContentLength);

public interface IJellyfinService
{
    /// <summary>Resolves a Sonarr/Radarr-identified show/movie to Jellyfin's own
    /// internal item, then builds a deep-link URL into Jellyfin's web player
    /// (routed through the CSP-stripping proxy — see JellyfinProxyBaseUrl —
    /// so it can actually be embedded in an iframe). Returns null if Jellyfin
    /// hasn't scanned/matched that item yet.</summary>
    Task<string?> GetTvEmbedUrlAsync(int tvdbId, int season, int episode, CancellationToken ct = default);

    Task<string?> GetMovieEmbedUrlAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>Proxies the movie's file down from Jellyfin's /Download endpoint —
    /// requires the configured API key to carry Jellyfin's "Allow media
    /// downloading" permission (unverified against the deployed instance; a 403
    /// here means that's off). Returns null if Jellyfin hasn't matched the movie.</summary>
    Task<JellyfinDownloadResult?> DownloadMovieAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>Same as DownloadMovieAsync, for a single episode.</summary>
    Task<JellyfinDownloadResult?> DownloadEpisodeAsync(int tvdbId, int season, int episode, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Jellyfin's REST API — same shape as SonarrService/
/// RadarrService. Only used at the moment of pressing Play; Sonarr/Radarr
/// remain the source of truth for "is this downloaded" (they already track
/// hasFile per episode/movie), Jellyfin is only asked "which of your items
/// is this" so we can link into its player.
/// </summary>
public class JellyfinService : IJellyfinService
{
    private readonly HttpClient _http;
    private readonly string _proxyBaseUrl;
    private string? _cachedServerId;

    public JellyfinService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        var baseUrl = configuration["Jellyfin:BaseUrl"];
        var apiKey = configuration["Jellyfin:ApiKey"];
        _proxyBaseUrl = (configuration["Jellyfin:ProxyBaseUrl"] ?? "").TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _http.BaseAddress = new Uri(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Remove("X-Emby-Token");
            _http.DefaultRequestHeaders.Add("X-Emby-Token", apiKey);
        }
    }

    public async Task<string?> GetTvEmbedUrlAsync(int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        if (episodeItemId is null) return null;

        var serverId = await GetServerIdAsync(ct);
        return BuildEmbedUrl(episodeItemId, serverId);
    }

    public async Task<string?> GetMovieEmbedUrlAsync(int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        if (movieId is null) return null;

        var serverId = await GetServerIdAsync(ct);
        return BuildEmbedUrl(movieId, serverId);
    }

    public async Task<JellyfinDownloadResult?> DownloadMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var movieId = await ResolveMovieItemIdAsync(tmdbId, ct);
        return movieId is null ? null : await FetchDownloadAsync(movieId, ct);
    }

    public async Task<JellyfinDownloadResult?> DownloadEpisodeAsync(int tvdbId, int season, int episode, CancellationToken ct = default)
    {
        var episodeItemId = await ResolveEpisodeItemIdAsync(tvdbId, season, episode, ct);
        return episodeItemId is null ? null : await FetchDownloadAsync(episodeItemId, ct);
    }

    private async Task<string?> ResolveMovieItemIdAsync(int tmdbId, CancellationToken ct)
    {
        var movieId = await FindItemIdByProviderAsync("Movie", "hasTmdbId", "Tmdb", tmdbId.ToString(), ct);
        if (movieId is null)
            Log.Information("[Jellyfin] No movie found matching TMDB id {TmdbId}", tmdbId);
        return movieId;
    }

    private async Task<string?> ResolveEpisodeItemIdAsync(int tvdbId, int season, int episode, CancellationToken ct)
    {
        var seriesId = await FindItemIdByProviderAsync("Series", "hasTvdbId", "Tvdb", tvdbId.ToString(), ct);
        if (seriesId is null)
        {
            Log.Information("[Jellyfin] No series found matching TVDB id {TvdbId}", tvdbId);
            return null;
        }

        var episodesResponse = await _http.GetAsync($"/Shows/{seriesId}/Episodes?fields=ProviderIds", ct);
        episodesResponse.EnsureSuccessStatusCode();
        var episodesDoc = await episodesResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var items = episodesDoc?["Items"] as JsonArray ?? [];

        var episodeItem = items.FirstOrDefault(item =>
            item is JsonObject obj &&
            (int?)obj["ParentIndexNumber"] == season &&
            (int?)obj["IndexNumber"] == episode);

        var episodeItemId = (episodeItem as JsonObject)?["Id"]?.ToString();
        if (episodeItemId is null)
            Log.Information("[Jellyfin] Series {SeriesId} found but no matching S{Season}E{Episode}", seriesId, season, episode);

        return episodeItemId;
    }

    private async Task<JellyfinDownloadResult> FetchDownloadAsync(string itemId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/Items/{itemId}/Download", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        var body = await response.Content.ReadAsStreamAsync(ct);

        return new JellyfinDownloadResult(
            body,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            fileName?.Trim('"'),
            response.Content.Headers.ContentLength);
    }

    // Jellyfin has no "give me the item with exactly this external ID" query
    // param (confirmed against its source — only has-any-ID booleans exist),
    // so this fetches everything with *some* ID from that provider and
    // filters client-side for the exact match.
    private async Task<string?> FindItemIdByProviderAsync(
        string includeItemType, string hasIdFilter, string providerKey, string providerValue, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/Items?IncludeItemTypes={includeItemType}&Recursive=true&{hasIdFilter}=true&fields=ProviderIds", ct);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var items = doc?["Items"] as JsonArray ?? [];

        foreach (var item in items)
        {
            if (item is not JsonObject obj) continue;
            var providerIds = obj["ProviderIds"] as JsonObject;
            if (providerIds?[providerKey]?.ToString() == providerValue)
                return obj["Id"]?.ToString();
        }

        return null;
    }

    private async Task<string> GetServerIdAsync(CancellationToken ct)
    {
        if (_cachedServerId is not null) return _cachedServerId;

        var response = await _http.GetAsync("/System/Info/Public", ct);
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        _cachedServerId = doc?["Id"]?.ToString() ?? throw new InvalidOperationException("Jellyfin did not return a server Id.");
        return _cachedServerId;
    }

    private string BuildEmbedUrl(string itemId, string serverId) =>
        $"{_proxyBaseUrl}/web/index.html#!/details?id={itemId}&serverId={serverId}&context=home&autoplay=true";
}
