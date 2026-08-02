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
    Task<List<SpotifyPlaylistDto>> GetUserPlaylistsForOwnerAsync(
        string ownerKey, CancellationToken token = default) => GetUserPlaylistsAsync(token);
    Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default);
    Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
        string playlistId, int offset = 0, int limit = 50, CancellationToken token = default);
    Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsForOwnerAsync(
        string ownerKey, string playlistId, int offset = 0, int limit = 50,
        CancellationToken token = default) => GetPlaylistItemsAsync(playlistId, offset, limit, token);
    Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
        CancellationToken token = default);
    Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksAsync(
        CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<SpotifyRecentTrackDto>>([]);
    Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksForOwnerAsync(
        string ownerKey, CancellationToken token = default) => GetRecentlyPlayedTracksAsync(token);
    Task<SpotifyTopItemsDto> GetTopItemsAsync(
        string kind = "tracks", string timeRange = "medium_term", int limit = 20,
        CancellationToken token = default);
    Task<SpotifyTopItemsDto> GetTopItemsForOwnerAsync(
        string ownerKey, string kind = "tracks", string timeRange = "medium_term", int limit = 20,
        CancellationToken token = default) => GetTopItemsAsync(kind, timeRange, limit, token);
    Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default);
    Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
    Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default);
    Task RemovePlaylistsFromLibraryAsync(List<string> playlistUris, CancellationToken token = default);

    // ─── phase 6/7 writes: every one returns the resulting snapshot ──────────
    Task<string?> AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken token = default);
    Task<string?> RemoveItemsAsync(
        string playlistId, IReadOnlyList<string> uris, string? snapshotId = null, CancellationToken token = default);
    Task<string?> ReplaceItemsAsync(
        string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default);
    Task<string?> ReorderItemsAsync(
        string playlistId, int rangeStart, int insertBefore, int rangeLength,
        string? snapshotId = null, CancellationToken token = default);
    Task ChangePlaylistDetailsAsync(
        string playlistId, string? name = null, string? description = null, bool? isPublic = null,
        CancellationToken token = default);
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

    public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) =>
        GetUserPlaylistsCoreAsync(ownerKey: null, token);

    public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsForOwnerAsync(
        string ownerKey, CancellationToken token = default) =>
        GetUserPlaylistsCoreAsync(ownerKey, token);

    private async Task<List<SpotifyPlaylistDto>> GetUserPlaylistsCoreAsync(
        string? ownerKey, CancellationToken token)
    {
        string? url = $"{ApiBaseUrl}/me/playlists?limit=50";
        var playlists = new List<SpotifyPlaylistDto>();
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        var connectedUserId = TryGetConnectedUserId(ownerKey);

        while (!string.IsNullOrWhiteSpace(url) && visitedPages.Add(url))
        {
            var response = await SendAuthenticatedRequestAsync<SpotifyPlaylistsResponse>(
                HttpMethod.Get, url, token, ownerKey: ownerKey);
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
    public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
        string playlistId,
        int offset = 0,
        int limit = 50,
        CancellationToken token = default) =>
        GetPlaylistItemsCoreAsync(ownerKey: null, playlistId, offset, limit, token);

    public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsForOwnerAsync(
        string ownerKey,
        string playlistId,
        int offset = 0,
        int limit = 50,
        CancellationToken token = default) =>
        GetPlaylistItemsCoreAsync(ownerKey, playlistId, offset, limit, token);

    private async Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsCoreAsync(
        string? ownerKey,
        string playlistId,
        int offset,
        int limit,
        CancellationToken token)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);

        var url = $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items?limit={limit}&offset={offset}";

        SpotifyPlaylistItemsResponse? response;
        try
        {
            response = await SendAuthenticatedRequestAsync<SpotifyPlaylistItemsResponse>(
                HttpMethod.Get, url, token, ownerKey: ownerKey);
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

    public Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksAsync(
        CancellationToken token = default) => GetRecentlyPlayedTracksCoreAsync(ownerKey: null, token);

    public Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksForOwnerAsync(
        string ownerKey, CancellationToken token = default) =>
        GetRecentlyPlayedTracksCoreAsync(ownerKey, token);

    private async Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksCoreAsync(
        string? ownerKey, CancellationToken token)
    {
        var response = await SendAuthenticatedRequestAsync<SpotifyRecentlyPlayedResponse>(
            HttpMethod.Get, $"{ApiBaseUrl}/me/player/recently-played?limit=50", token,
            ownerKey: ownerKey);

        return (response?.Items ?? [])
            .Where(entry => entry.Track != null)
            .Select((entry, index) => new SpotifyRecentTrackDto(
                MapTrackItem(entry.Track!, index, entry.PlayedAt),
                entry.PlayedAt,
                entry.Context?.Type,
                entry.Context?.Uri))
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
        CancellationToken token = default) =>
        await GetTopItemsCoreAsync(ownerKey: null, kind, timeRange, limit, token);

    public Task<SpotifyTopItemsDto> GetTopItemsForOwnerAsync(
        string ownerKey,
        string kind = "tracks",
        string timeRange = "medium_term",
        int limit = 20,
        CancellationToken token = default) =>
        GetTopItemsCoreAsync(ownerKey, kind, timeRange, limit, token);

    private async Task<SpotifyTopItemsDto> GetTopItemsCoreAsync(
        string? ownerKey,
        string kind,
        string timeRange,
        int limit,
        CancellationToken token)
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
            token,
            ownerKey: ownerKey);

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
            AddedAt: entry.AddedAt,
            Isrc: item.ExternalIds?.Isrc);
    }

    private static SpotifyPlaylistItemDto MapTrackItem(
        SpotifyTrackItem item, int position, DateTimeOffset? observedAt) =>
        new(
            position,
            item.IsLocal ? SpotifyItemKind.Local : SpotifyItemKind.Track,
            item.Id,
            item.Name,
            item.Uri,
            string.Join(", ", item.Artists.Select(a => a.Name)),
            item.Album.Name,
            item.DurationMs,
            item.ExternalUrls?.Spotify,
            item.IsLocal,
            observedAt,
            item.ExternalIds?.Isrc);

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

    /// <summary>
    /// Spotify accepts at most 100 URIs per add. Batching is not an optimisation —
    /// a 300-track draft simply fails without it. Each batch returns a snapshot; the
    /// last one is what the playlist now looks like.
    /// </summary>
    public async Task<string?> AddItemsAsync(
        string playlistId, IReadOnlyList<string> uris, CancellationToken token = default)
    {
        string? snapshot = null;

        foreach (var batch in uris.Where(u => !string.IsNullOrWhiteSpace(u)).Chunk(100))
        {
            var response = await SendAuthenticatedRequestAsync<SpotifySnapshotResponse>(
                HttpMethod.Post,
                $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items",
                token,
                new { uris = batch });

            snapshot = response?.SnapshotId ?? snapshot;
        }

        Log.Information("[Spotify] Added {Count} item(s) to playlist {PlaylistId}", uris.Count, playlistId);
        return snapshot;
    }

    /// <summary>
    /// Removes every occurrence of each URI. Passing the snapshot we planned against
    /// makes Spotify reject the call if the playlist changed in between, which is the
    /// behaviour we want: better a conflict than deleting whatever moved into that slot.
    /// </summary>
    public async Task<string?> RemoveItemsAsync(
        string playlistId, IReadOnlyList<string> uris, string? snapshotId = null, CancellationToken token = default)
    {
        string? snapshot = snapshotId;

        foreach (var batch in uris.Where(u => !string.IsNullOrWhiteSpace(u)).Chunk(100))
        {
            object body = snapshot is null
                ? new { items = batch.Select(uri => new { uri }).ToArray() }
                : new { items = batch.Select(uri => new { uri }).ToArray(), snapshot_id = snapshot };

            var response = await SendAuthenticatedRequestAsync<SpotifySnapshotResponse>(
                HttpMethod.Delete,
                $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items",
                token,
                body);

            snapshot = response?.SnapshotId ?? snapshot;
        }

        Log.Information("[Spotify] Removed {Count} item(s) from playlist {PlaylistId}", uris.Count, playlistId);
        return snapshot;
    }

    /// <summary>
    /// Replaces the whole item list. The first PUT of up to 100 URIs *is* the
    /// replacement — an empty list clears the playlist — and any remainder is appended,
    /// because Spotify has no single call that sets more than 100 at once.
    /// </summary>
    public async Task<string?> ReplaceItemsAsync(
        string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default)
    {
        var clean = orderedUris.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        var url = $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items";

        var first = await SendAuthenticatedRequestAsync<SpotifySnapshotResponse>(
            HttpMethod.Put, url, token, new { uris = clean.Take(100).ToArray() });

        var snapshot = first?.SnapshotId;

        if (clean.Count > 100)
        {
            var appended = await AddItemsAsync(playlistId, clean.Skip(100).ToList(), token);
            snapshot = appended ?? snapshot;
        }

        Log.Information("[Spotify] Replaced playlist {PlaylistId} with {Count} item(s)", playlistId, clean.Count);
        return snapshot;
    }

    public async Task<string?> ReorderItemsAsync(
        string playlistId, int rangeStart, int insertBefore, int rangeLength,
        string? snapshotId = null, CancellationToken token = default)
    {
        object body = snapshotId is null
            ? new { range_start = rangeStart, insert_before = insertBefore, range_length = Math.Max(1, rangeLength) }
            : new
            {
                range_start = rangeStart,
                insert_before = insertBefore,
                range_length = Math.Max(1, rangeLength),
                snapshot_id = snapshotId
            };

        var response = await SendAuthenticatedRequestAsync<SpotifySnapshotResponse>(
            HttpMethod.Put, $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/items", token, body);

        Log.Information("[Spotify] Reordered playlist {PlaylistId}", playlistId);
        return response?.SnapshotId;
    }

    /// <summary>
    /// Only the fields the caller actually wants changed are sent. Spotify treats an
    /// omitted field as "leave alone", so sending nulls wholesale would blank a
    /// description nobody asked to touch.
    /// </summary>
    public async Task ChangePlaylistDetailsAsync(
        string playlistId, string? name = null, string? description = null, bool? isPublic = null,
        CancellationToken token = default)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name;
        if (description is not null) body["description"] = description;
        if (isPublic is not null) body["public"] = isPublic.Value;

        if (body.Count == 0)
            return;

        await SendAuthenticatedRequestAsync<object>(
            HttpMethod.Put, $"{ApiBaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}", token, body);

        Log.Information("[Spotify] Updated details of playlist {PlaylistId}", playlistId);
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    private async Task<T?> SendAuthenticatedRequestAsync<T>(
        HttpMethod method,
        string url,
        CancellationToken token,
        object? body = null,
        string? ownerKey = null) where T : class
    {
        var serializedBody = body == null ? null : JsonSerializer.Serialize(body);
        var forceRefreshNext = false;
        var authenticationRetries = 0;
        var rateLimitRetries = 0;

        while (true)
        {
            string accessToken;
            try
            {
                accessToken = ownerKey == null
                    ? await _accessTokens.GetAccessTokenAsync(forceRefreshNext, token)
                    : await _accessTokens.GetAccessTokenForOwnerAsync(ownerKey, forceRefreshNext, token);
                forceRefreshNext = false;
            }
            catch (SpotifyConnectionException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
                ex.RetryAfter is { } providerDelay &&
                providerDelay <= TimeSpan.FromSeconds(30) &&
                rateLimitRetries < 3)
            {
                rateLimitRetries++;
                await DelayForRateLimitAsync(providerDelay, token);
                continue;
            }

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
                if (ownerKey == null)
                    await _accessTokens.RecordUnavailableAsync(ex.Message, token);
                else
                    await _accessTokens.RecordUnavailableForOwnerAsync(ownerKey, ex.Message, token);
                throw new SpotifyConnectionException(
                    "Spotify is currently unavailable.",
                    nameof(SpotifyConnectionState.SpotifyUnavailable),
                    System.Net.HttpStatusCode.BadGateway);
            }

            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && authenticationRetries == 0)
                {
                    authenticationRetries++;
                    forceRefreshNext = true;
                    continue;
                }

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
                    if (ownerKey == null)
                        await _accessTokens.RecordApiFailureAsync(exception, token);
                    else
                        await _accessTokens.RecordApiFailureForOwnerAsync(ownerKey, exception, token);

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
                        !exception.IsQuotaExceeded &&
                        retryAfter is { } responseDelay &&
                        responseDelay <= TimeSpan.FromSeconds(30) &&
                        rateLimitRetries < 3)
                    {
                        rateLimitRetries++;
                        await DelayForRateLimitAsync(responseDelay, token);
                        continue;
                    }

                    throw exception;
                }

                if (ownerKey == null)
                    await _accessTokens.RecordSuccessfulCallAsync(token);
                else
                    await _accessTokens.RecordSuccessfulCallForOwnerAsync(ownerKey, token);

                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return null;

                var content = await response.Content.ReadAsStringAsync(token);
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                return JsonSerializer.Deserialize<T>(content);
            }
        }
    }

    private static Task DelayForRateLimitAsync(TimeSpan retryAfter, CancellationToken token)
    {
        // Spotify's integer Retry-After can expire on the boundary. A small cushion
        // avoids immediately receiving the same 429 without materially extending
        // an inventory scan.
        var delay = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
        return Task.Delay(delay + TimeSpan.FromMilliseconds(150), token);
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
    private string? TryGetConnectedUserId(string? ownerKey = null)
    {
        try
        {
            return ownerKey == null
                ? _accessTokens.GetConnectedSpotifyUserId()
                : _accessTokens.GetConnectedSpotifyUserId(ownerKey);
        }
        catch (SpotifyConnectionException)
        {
            return null;
        }
    }
}
