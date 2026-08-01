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
    Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default);
    Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
        string playlistId, int offset = 0, int limit = 50, CancellationToken token = default);
    Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
        CancellationToken token = default);
    Task<SpotifyTopItemsDto> GetTopItemsAsync(
        string kind = "tracks", string timeRange = "medium_term", int limit = 20,
        CancellationToken token = default);
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
        var connectedUserId = TryGetConnectedUserId();

        while (!string.IsNullOrWhiteSpace(url) && visitedPages.Add(url))
        {
            var response = await SendAuthenticatedRequestAsync<SpotifyPlaylistsResponse>(HttpMethod.Get, url, token);
            if (response == null)
                break;

            playlists.AddRange(response.Items.Select(item => MapPlaylistToDto(item, connectedUserId)));
            url = response.Next;
        }

        return playlists;
    }

    public async Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return null;

        var playlist = await SendAuthenticatedRequestAsync<SpotifyPlaylistItem>(
            HttpMethod.Get, $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}", token);

        return playlist == null ? null : MapPlaylistToDto(playlist, TryGetConnectedUserId());
    }

    /// <summary>
    /// One page of playlist contents. A 403 here is a real answer, not a failure:
    /// Spotify exposes metadata for playlists you merely follow but refuses their
    /// items. That returns an empty page marked <see cref="SpotifyContentsAccess.Forbidden"/>
    /// so callers can say "not allowed to read" rather than "no songs".
    /// </summary>
    public async Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
        string playlistId,
        int offset = 0,
        int limit = 50,
        CancellationToken token = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);

        var url = $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items?limit={limit}&offset={offset}";

        SpotifyPlaylistItemsResponse? response;
        try
        {
            response = await SendAuthenticatedRequestAsync<SpotifyPlaylistItemsResponse>(
                HttpMethod.Get, url, token);
        }
        catch (SpotifyApiException ex) when (ex.SpotifyStatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Log.Information(
                "[Spotify] Contents of playlist {PlaylistId} are not accessible to this account", playlistId);
            return new SpotifyPlaylistItemsPageDto(
                playlistId, [], Total: 0, offset, limit, HasMore: false,
                Access: SpotifyContentsAccess.Forbidden);
        }

        if (response?.Items == null)
        {
            return new SpotifyPlaylistItemsPageDto(
                playlistId, [], Total: 0, offset, limit, HasMore: false,
                Access: SpotifyContentsAccess.Unavailable);
        }

        var items = response.Items
            .Select((entry, index) => MapPlaylistItem(entry, offset + index))
            .ToList();

        return new SpotifyPlaylistItemsPageDto(
            playlistId,
            items,
            response.Total,
            response.Offset,
            response.Limit,
            HasMore: !string.IsNullOrWhiteSpace(response.Next));
    }

    /// <summary>
    /// How often each playlist shows up as the playback context in the recent
    /// history Spotify returns. Plays with no context, or an album/artist context,
    /// are skipped — they are absence of evidence, so counting them as zero for a
    /// playlist would invent a fact.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
        CancellationToken token = default)
    {
        var response = await SendAuthenticatedRequestAsync<SpotifyRecentlyPlayedResponse>(
            HttpMethod.Get, $"{ApiBaseUrl}/me/player/recently-played?limit=50", token);

        if (response?.Items == null)
            return [];

        return response.Items
            .Select(entry => entry.Context)
            .Where(context => string.Equals(context?.Type, "playlist", StringComparison.OrdinalIgnoreCase))
            .Select(context => (Id: PlaylistIdFromUri(context!.Uri), context.ExternalUrls?.Spotify))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id!, StringComparer.Ordinal)
            .Select(group => new SpotifyRecentPlaylistContextDto(
                group.Key,
                Name: null,
                ObservedPlays: group.Count(),
                SpotifyUrl: group.Select(x => x.Spotify).FirstOrDefault(url => url != null)))
            .OrderByDescending(x => x.ObservedPlays)
            .ThenBy(x => x.PlaylistId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Top tracks or artists over one of Spotify's three windows. Anything Spotify
    /// does not recognise is corrected to a supported value rather than passed
    /// through, because a rejected request here reads to the user as "no history".
    /// </summary>
    public async Task<SpotifyTopItemsDto> GetTopItemsAsync(
        string kind = "tracks",
        string timeRange = "medium_term",
        int limit = 20,
        CancellationToken token = default)
    {
        var safeKind = string.Equals(kind, "artists", StringComparison.OrdinalIgnoreCase)
            ? "artists"
            : "tracks";

        var safeRange = timeRange?.ToLowerInvariant() switch
        {
            "short_term" => "short_term",
            "long_term" => "long_term",
            _ => "medium_term"
        };

        limit = Math.Clamp(limit, 1, 50);

        var response = await SendAuthenticatedRequestAsync<SpotifyTopItemsResponse>(
            HttpMethod.Get,
            $"{ApiBaseUrl}/me/top/{safeKind}?time_range={safeRange}&limit={limit}",
            token);

        var items = (response?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select((item, index) => new SpotifyTopItemDto(
                item.Id!,
                item.Name ?? "Unknown",
                // A track is described by its artists; an artist by its genres.
                Detail: item.Artists is { Count: > 0 }
                    ? string.Join(", ", item.Artists.Select(a => a.Name))
                    : item.Genres is { Count: > 0 }
                        ? string.Join(", ", item.Genres.Take(3))
                        : null,
                item.ExternalUrls?.Spotify,
                Rank: index + 1))
            .ToList();

        return new SpotifyTopItemsDto(safeKind, safeRange, items);
    }

    /// <summary>spotify:playlist:37i9dQ → 37i9dQ. Returns null for any other URI shape.</summary>
    private static string? PlaylistIdFromUri(string? uri)
    {
        const string prefix = "spotify:playlist:";
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var id = uri[prefix.Length..];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static SpotifyPlaylistItemDto MapPlaylistItem(SpotifyPlaylistEntry entry, int position)
    {
        var item = entry.Item;

        // A null item is not corrupt data — Spotify returns it for content that has
        // been removed from the catalog since it was added to the playlist.
        if (item == null)
        {
            return new SpotifyPlaylistItemDto(
                position, SpotifyItemKind.Unavailable, null, null, null,
                Artists: "", AlbumName: null, DurationMs: 0, SpotifyUrl: null,
                IsLocal: entry.IsLocal, AddedAt: entry.AddedAt);
        }

        var isLocal = entry.IsLocal || item.IsLocal;
        var kind = isLocal
            ? SpotifyItemKind.Local
            : string.Equals(item.Type, "episode", StringComparison.OrdinalIgnoreCase)
                ? SpotifyItemKind.Episode
                : SpotifyItemKind.Track;

        return new SpotifyPlaylistItemDto(
            position,
            kind,
            item.Id,
            item.Name,
            item.Uri,
            // Episodes carry no artists array; joining an empty list is correct and
            // an empty string renders as "no artist" rather than "null".
            Artists: item.Artists == null ? "" : string.Join(", ", item.Artists.Select(a => a.Name)),
            AlbumName: item.Album?.Name,
            DurationMs: item.DurationMs,
            SpotifyUrl: item.ExternalUrls?.Spotify,
            IsLocal: isLocal,
            AddedAt: entry.AddedAt);
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

    /// <summary>
    /// Note <c>TrackCount: playlist.ItemSummary?.Total</c> — no <c>?? 0</c>. A missing
    /// <c>items</c> summary means Spotify declined to say, and the null survives all
    /// the way to the UI so it can render "unavailable" instead of inventing a zero.
    ///
    /// <paramref name="connectedUserId"/> is passed in rather than resolved here:
    /// reading it costs a SQLite hit and an AES decrypt, and this runs once per
    /// playlist. Resolving it inside would mean 100+ decrypts for one listing.
    /// </summary>
    private static SpotifyPlaylistDto MapPlaylistToDto(SpotifyPlaylistItem playlist, string? connectedUserId)
    {
        return new SpotifyPlaylistDto(
            playlist.Id,
            playlist.Name,
            playlist.Images?.FirstOrDefault()?.Url,
            playlist.ItemSummary?.Total,
            playlist.ExternalUrls?.Spotify,
            ContentsAvailable: playlist.ItemSummary != null,
            SnapshotId: playlist.SnapshotId,
            OwnerId: playlist.Owner?.Id,
            OwnerName: playlist.Owner?.DisplayName,
            IsOwnedByUser: connectedUserId != null
                && string.Equals(playlist.Owner?.Id, connectedUserId, StringComparison.Ordinal),
            IsCollaborative: playlist.Collaborative,
            IsPublic: playlist.Public,
            Uri: playlist.Uri);
    }

    /// <summary>
    /// Ownership is a display and safety hint, not the thing standing between a
    /// caller and someone else's playlist — Spotify enforces that itself. If the
    /// connection cannot be read, report "not owned" rather than failing the whole
    /// listing.
    /// </summary>
    private string? TryGetConnectedUserId()
    {
        try
        {
            return _accessTokens.GetConnectedSpotifyUserId();
        }
        catch (SpotifyConnectionException)
        {
            return null;
        }
    }
}
