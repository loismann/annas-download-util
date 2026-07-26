using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>Result of proxying a range-capable request (audio file or cover)
/// through to Audiobookshelf — carries just enough of the upstream response
/// for the endpoint layer to relay it back verbatim.</summary>
public record AudiobookshelfStreamResult(Stream Body, string ContentType, string? ContentRange, long? ContentLength, int StatusCode);

public interface IAudiobookshelfService
{
    /// <summary>Every item in the single Audiobookshelf library this app is
    /// configured against (resolved once, cached — see ResolveLibraryIdAsync),
    /// raw JSON as Audiobookshelf returns it.</summary>
    Task<JsonArray> GetLibraryItemsAsync(CancellationToken ct = default);

    /// <summary>Full item detail (chapters, per-file duration, narrator/series
    /// metadata) — the list endpoint above returns a lighter-weight shape.</summary>
    Task<JsonObject?> GetItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>Proxies one of the item's audio files, forwarding the
    /// incoming Range header so seeking works — Audiobookshelf does the
    /// actual byte-range slicing, this just relays its response.</summary>
    Task<AudiobookshelfStreamResult> StreamAudioFileAsync(string itemId, string ino, string? rangeHeader, CancellationToken ct = default);

    Task<AudiobookshelfStreamResult> GetCoverAsync(string itemId, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Audiobookshelf's REST API — mirrors RadarrService/
/// SonarrService/JellyfinService's shape. Audiobookshelf owns all folder-
/// structure parsing, audio metadata/chapter extraction, and cover art; this
/// app never touches the audiobook files directly, same as it doesn't touch
/// Sonarr/Radarr/Jellyfin's managed files.
///
/// Auth: assumes the deployed Audiobookshelf version supports a static API
/// key (Settings -> Users -> API Keys), sent as a Bearer token exactly like
/// Radarr/Sonarr's X-Api-Key header — verify this against the actual pinned
/// image version during setup; older Audiobookshelf versions only support
/// username/password login, which would need a small login/token-cache
/// addition here if hit.
/// </summary>
public class AudiobookshelfService : IAudiobookshelfService
{
    private readonly HttpClient _http;
    private string? _cachedLibraryId;

    public AudiobookshelfService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        var baseUrl = configuration["Audiobookshelf:BaseUrl"];
        var apiKey = configuration["Audiobookshelf:ApiKey"];

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _http.BaseAddress = new Uri(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<JsonArray> GetLibraryItemsAsync(CancellationToken ct = default)
    {
        var libraryId = await ResolveLibraryIdAsync(ct);
        if (libraryId is null) return [];

        var response = await _http.GetAsync($"/api/libraries/{libraryId}/items?limit=0", ct);
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return doc?["results"] as JsonArray ?? [];
    }

    public async Task<JsonObject?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/items/{Uri.EscapeDataString(itemId)}?expanded=1", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
    }

    public async Task<AudiobookshelfStreamResult> StreamAudioFileAsync(string itemId, string ino, string? rangeHeader, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/items/{Uri.EscapeDataString(itemId)}/file/{Uri.EscapeDataString(ino)}");
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ToStreamResultAsync(response, ct);
    }

    public async Task<AudiobookshelfStreamResult> GetCoverAsync(string itemId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/api/items/{Uri.EscapeDataString(itemId)}/cover", HttpCompletionOption.ResponseHeadersRead, ct);
        return await ToStreamResultAsync(response, ct);
    }

    private static async Task<AudiobookshelfStreamResult> ToStreamResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStreamAsync(ct);
        return new AudiobookshelfStreamResult(
            body,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            response.Content.Headers.ContentRange?.ToString(),
            response.Content.Headers.ContentLength,
            (int)response.StatusCode);
    }

    /// <summary>Resolves the single Audiobookshelf library's id and caches it
    /// in memory (same idiom as JellyfinService's _cachedServerId) — this app
    /// is only ever configured with one library (the mounted /audiobooks
    /// folder), so there's no per-request library selection to do.</summary>
    private async Task<string?> ResolveLibraryIdAsync(CancellationToken ct)
    {
        if (_cachedLibraryId is not null) return _cachedLibraryId;

        var response = await _http.GetAsync("/api/libraries", ct);
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        var libraries = doc?["libraries"] as JsonArray ?? [];

        var libraryId = (libraries.FirstOrDefault() as JsonObject)?["id"]?.ToString();
        if (libraryId is null)
        {
            Log.Warning("[Audiobookshelf] No library configured yet — add one pointing at /audiobooks in Audiobookshelf's admin UI first.");
            return null;
        }

        _cachedLibraryId = libraryId;
        return libraryId;
    }
}
