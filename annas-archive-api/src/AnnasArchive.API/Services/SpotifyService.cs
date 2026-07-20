using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Models;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Services;

public interface ISpotifyService
{
    Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 20, CancellationToken token = default);
    Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default);
    Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default);
    Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
    Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
}

public class SpotifyService : ISpotifyService
{
    private readonly HttpClient _httpClient;
    private readonly SpotifyConfiguration _config;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string ApiBaseUrl = "https://api.spotify.com/v1";

    public SpotifyService(HttpClient httpClient, IOptions<SpotifyConfiguration> config)
    {
        _httpClient = httpClient;
        _config = config.Value;
    }

    public async Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 20, CancellationToken token = default)
    {
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
        var url = $"{ApiBaseUrl}/me/playlists?limit=50";
        var response = await SendAuthenticatedRequestAsync<SpotifyPlaylistsResponse>(HttpMethod.Get, url, token);

        if (response == null)
            return [];

        return response.Items.Select(p => new SpotifyPlaylistDto(
            p.Id,
            p.Name,
            p.Images?.FirstOrDefault()?.Url,
            p.Tracks?.Total ?? 0,
            p.ExternalUrls?.Spotify
        )).ToList();
    }

    public async Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default)
    {
        var userId = await GetCurrentUserIdAsync(token);
        var url = $"{ApiBaseUrl}/users/{userId}/playlists";

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

        var url = $"{ApiBaseUrl}/playlists/{playlistId}/tracks";
        var body = new { uris = trackUris };

        await SendAuthenticatedRequestAsync<object>(HttpMethod.Post, url, token, body);
        Log.Information("[Spotify] Added {Count} tracks to playlist {PlaylistId}", trackUris.Count, playlistId);
    }

    public async Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default)
    {
        if (trackUris.Count == 0) return;

        var url = $"{ApiBaseUrl}/playlists/{playlistId}/tracks";
        var body = new
        {
            tracks = trackUris.Select(uri => new { uri }).ToList()
        };

        await SendAuthenticatedRequestAsync<object>(HttpMethod.Delete, url, token, body);
        Log.Information("[Spotify] Removed {Count} tracks from playlist {PlaylistId}", trackUris.Count, playlistId);
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    private async Task<string> GetCurrentUserIdAsync(CancellationToken token)
    {
        var url = $"{ApiBaseUrl}/me";
        var response = await SendAuthenticatedRequestAsync<SpotifyUserProfile>(HttpMethod.Get, url, token);

        if (response == null)
            throw new InvalidOperationException("Failed to get current user profile");

        return response.Id;
    }

    private async Task<T?> SendAuthenticatedRequestAsync<T>(
        HttpMethod method,
        string url,
        CancellationToken token,
        object? body = null) where T : class
    {
        await EnsureValidTokenAsync(token);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, token);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(token);
            Log.Warning("[Spotify] API error {StatusCode}: {Error}", response.StatusCode, errorContent);
            throw new HttpRequestException($"Spotify API error: {response.StatusCode}");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        var content = await response.Content.ReadAsStringAsync(token);
        return JsonSerializer.Deserialize<T>(content);
    }

    private async Task EnsureValidTokenAsync(CancellationToken token)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            return;

        await _tokenLock.WaitAsync(token);
        try
        {
            // Double-check after acquiring lock
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return;

            await RefreshAccessTokenAsync(token);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task RefreshAccessTokenAsync(CancellationToken token)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_config.ClientId}:{_config.ClientSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _config.RefreshToken
        });

        var response = await _httpClient.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("[Spotify] Token refresh failed: {Error}", content);
            throw new InvalidOperationException($"Failed to refresh Spotify token: {content}");
        }

        var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(content);
        if (tokenResponse == null)
            throw new InvalidOperationException("Failed to parse token response");

        _accessToken = tokenResponse.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        Log.Debug("[Spotify] Access token refreshed, expires at {Expiry}", _tokenExpiry);
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
}
