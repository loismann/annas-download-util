using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

public interface ISpotifyService
{
    Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 10, CancellationToken token = default);
    Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default);
    Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default);
    Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
    Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
    Task RemovePlaylistsFromLibraryAsync(List<string> playlistUris, CancellationToken token = default);
}

public class SpotifyService : ISpotifyService
{
    private readonly HttpClient _httpClient;
    private readonly ISpotifyAccessTokenProvider _accessTokens;
    private const string ApiBaseUrl = "https://api.spotify.com/v1";

    public SpotifyService(HttpClient httpClient, ISpotifyAccessTokenProvider accessTokens)
    {
        _httpClient = httpClient;
        _accessTokens = accessTokens;
    }

    public async Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 10, CancellationToken token = default)
    {
        limit = Math.Clamp(limit, 1, 10);
        var encodedQuery = Uri.EscapeDataString(query);
        var url = $"{ApiBaseUrl}/search?q={encodedQuery}&type=track&limit={limit}";

        var response = await SendAuthenticatedRequestAsync<SpotifySearchResponse>(HttpMethod.Get, url, token);

        if (response?.Tracks == null)
            return new SpotifySearchResultDto([], 0);

        var tracks = response.Tracks.Items.Select(MapToDto).ToList();
        return new SpotifySearchResultDto(tracks, response.Tracks.Total);
    }

    public async Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default)
    {
        string? url = $"{ApiBaseUrl}/me/playlists?limit=50";
        var playlists = new List<SpotifyPlaylistDto>();
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);

        while (!string.IsNullOrWhiteSpace(url) && visitedPages.Add(url))
        {
            var response = await SendAuthenticatedRequestAsync<SpotifyPlaylistsResponse>(HttpMethod.Get, url, token);
            if (response == null)
                break;

            playlists.AddRange(response.Items.Select(MapPlaylistToDto));
            url = response.Next;
        }

        return playlists;
    }

    public async Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default)
    {
        var url = $"{ApiBaseUrl}/me/playlists";

        var body = new
        {
            name,
            description = description ?? "",
            @public = isPublic
        };

        var response = await SendAuthenticatedRequestAsync<SpotifyPlaylistResponse>(
            HttpMethod.Post, url, token, body);

        if (response == null)
            throw new InvalidOperationException("Failed to create playlist");

        Log.Information("[Spotify] Created playlist: {PlaylistName} ({PlaylistId})", name, response.Id);

        return new SpotifyPlaylistDto(
            response.Id,
            response.Name,
            null,
            0,
            response.ExternalUrls?.Spotify
        );
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default)
    {
        if (trackUris.Count == 0) return;

        var url = $"{ApiBaseUrl}/playlists/{playlistId}/items";
        var body = new { uris = trackUris };

        await SendAuthenticatedRequestAsync<object>(HttpMethod.Post, url, token, body);
        Log.Information("[Spotify] Added {Count} tracks to playlist {PlaylistId}", trackUris.Count, playlistId);
    }

    public async Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default)
    {
        if (trackUris.Count == 0) return;

        var url = $"{ApiBaseUrl}/playlists/{playlistId}/items";
        var body = new
        {
            items = trackUris.Select(uri => new { uri }).ToList()
        };

        await SendAuthenticatedRequestAsync<object>(HttpMethod.Delete, url, token, body);
        Log.Information("[Spotify] Removed {Count} tracks from playlist {PlaylistId}", trackUris.Count, playlistId);
    }

    public async Task RemovePlaylistsFromLibraryAsync(
        List<string> playlistUris,
        CancellationToken token = default)
    {
        foreach (var batch in playlistUris
                     .Where(uri => !string.IsNullOrWhiteSpace(uri))
                     .Distinct(StringComparer.Ordinal)
                     .Chunk(40))
        {
            var uris = Uri.EscapeDataString(string.Join(',', batch));
            await SendAuthenticatedRequestAsync<object>(
                HttpMethod.Delete,
                $"{ApiBaseUrl}/me/library?uris={uris}",
                token);
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    private async Task<T?> SendAuthenticatedRequestAsync<T>(
        HttpMethod method,
        string url,
        CancellationToken token,
        object? body = null) where T : class
    {
        var serializedBody = body == null ? null : JsonSerializer.Serialize(body);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var accessToken = await _accessTokens.GetAccessTokenAsync(
                forceRefresh: attempt > 0,
                token);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            if (serializedBody != null)
                request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, token);
            }
            catch (HttpRequestException ex)
            {
                await _accessTokens.RecordUnavailableAsync(ex.Message, token);
                throw new SpotifyConnectionException(
                    "Spotify is currently unavailable.",
                    nameof(SpotifyConnectionState.SpotifyUnavailable),
                    System.Net.HttpStatusCode.BadGateway);
            }

            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
                    continue;

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(token);
                    var (spotifyMessage, reason) = ParseSpotifyError(errorContent);
                    var retryAfter = response.Headers.RetryAfter?.Delta;

                    if (retryAfter == null && response.Headers.RetryAfter?.Date is { } retryDate)
                    {
                        var delay = retryDate - DateTimeOffset.UtcNow;
                        retryAfter = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                    }

                    Log.Warning(
                        "[Spotify] API error {StatusCode}: {Message} (reason: {Reason}, retry after: {RetryAfter})",
                        response.StatusCode,
                        spotifyMessage,
                        reason,
                        retryAfter);

                    var exception = new SpotifyApiException(
                        response.StatusCode,
                        spotifyMessage,
                        reason,
                        retryAfter);
                    await _accessTokens.RecordApiFailureAsync(exception, token);
                    throw exception;
                }

                await _accessTokens.RecordSuccessfulCallAsync(token);

                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return null;

                var content = await response.Content.ReadAsStringAsync(token);
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                return JsonSerializer.Deserialize<T>(content);
            }
        }

        throw new InvalidOperationException("Spotify request retry flow ended unexpectedly.");
    }

    private static (string? Message, string? Reason) ParseSpotifyError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var nestedError) ? nestedError : root;

            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;

            var reason = error.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : root.TryGetProperty("reason", out var rootReasonElement)
                    ? rootReasonElement.GetString()
                    : null;

            return (message, reason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static SpotifyTrackDto MapToDto(SpotifyTrackItem track)
    {
        var artists = string.Join(", ", track.Artists.Select(a => a.Name));
        var albumArt = track.Album.Images.FirstOrDefault()?.Url;

        return new SpotifyTrackDto(
            track.Id,
            track.Name,
            track.Uri,
            track.DurationMs,
            artists,
            track.Album.Name,
            albumArt,
            track.ExternalUrls?.Spotify
        );
    }

    private static SpotifyPlaylistDto MapPlaylistToDto(SpotifyPlaylistItem playlist) => new(
        playlist.Id,
        playlist.Name,
        playlist.Images?.FirstOrDefault()?.Url,
        playlist.ItemSummary?.Total ?? 0,
        playlist.ExternalUrls?.Spotify,
        ContentsAvailable: playlist.ItemSummary != null,
        SnapshotId: playlist.SnapshotId
    );
}
