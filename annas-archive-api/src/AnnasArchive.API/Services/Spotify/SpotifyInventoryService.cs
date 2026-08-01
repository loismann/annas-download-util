using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyInventoryService
{
    Task<SpotifyPlaylistContents> GetContentsAsync(
        SpotifyPlaylistDto playlist, CancellationToken token = default);

    Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
        IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default);
}

/// <summary>
/// Fetches complete playlist contents, and remembers them against the playlist's
/// snapshot ID.
///
/// A snapshot ID changes whenever a playlist's items change, so an unchanged
/// snapshot means a cached copy is still exactly right — no staleness window and no
/// TTL guesswork. That matters here because analysing a 100+ playlist library means
/// paging every one of them, and doing that on every question would be both slow
/// and a good way to meet Spotify's rate limit.
/// </summary>
public sealed class SpotifyInventoryService : ISpotifyInventoryService
{
    /// <summary>
    /// Playlists fetched at once during a bulk scan. The spec's default; low enough
    /// to stay polite, high enough that a large library does not take minutes.
    /// </summary>
    private const int ScanConcurrency = 2;

    private const int PageSize = 50;

    /// <summary>
    /// Guards against a runaway playlist consuming the whole scan. 10k items is far
    /// beyond anything real; hitting it means something is wrong, not that a user
    /// has a big playlist.
    /// </summary>
    private const int MaxItemsPerPlaylist = 10_000;

    private readonly ISpotifyService _spotify;
    private readonly LruCache<string, CacheEntry> _cache = new(capacity: 500, ttl: TimeSpan.FromHours(6));

    public SpotifyInventoryService(ISpotifyService spotify)
    {
        _spotify = spotify;
    }

    public async Task<SpotifyPlaylistContents> GetContentsAsync(
        SpotifyPlaylistDto playlist, CancellationToken token = default)
    {
        // No snapshot means nothing to key a cache on — always refetch rather than
        // risk serving a copy we cannot prove is current.
        if (!string.IsNullOrWhiteSpace(playlist.SnapshotId)
            && _cache.TryGetValue(playlist.Id, out var cached)
            && cached!.SnapshotId == playlist.SnapshotId)
        {
            return new SpotifyPlaylistContents(playlist, cached.Items, cached.Access, cached.SnapshotId);
        }

        var contents = await FetchAllPagesAsync(playlist, token);

        if (!string.IsNullOrWhiteSpace(playlist.SnapshotId))
        {
            _cache.Set(playlist.Id, new CacheEntry(playlist.SnapshotId!, contents.Items, contents.Access));
        }

        return contents;
    }

    public async Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
        IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default)
    {
        var results = new SpotifyPlaylistContents[playlists.Count];
        using var gate = new SemaphoreSlim(ScanConcurrency);

        var work = playlists.Select(async (playlist, index) =>
        {
            await gate.WaitAsync(token);
            try
            {
                results[index] = await GetContentsAsync(playlist, token);
            }
            catch (SpotifyApiException ex)
            {
                // One playlist failing must not lose the other ninety-nine. It is
                // recorded as unreadable, which analysis already knows how to report.
                Log.Warning("[Spotify] Could not read playlist {PlaylistId}: {Message}",
                    playlist.Id, ex.SpotifyMessage);
                results[index] = new SpotifyPlaylistContents(
                    playlist, [], SpotifyContentsAccess.Unavailable, playlist.SnapshotId);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work);
        return results;
    }

    private async Task<SpotifyPlaylistContents> FetchAllPagesAsync(
        SpotifyPlaylistDto playlist, CancellationToken token)
    {
        var items = new List<SpotifyPlaylistItemDto>();
        var offset = 0;

        while (true)
        {
            var page = await _spotify.GetPlaylistItemsAsync(playlist.Id, offset, PageSize, token);

            if (page.Access != SpotifyContentsAccess.Available)
            {
                // Only trust an access verdict from the first page. Later pages
                // failing is a partial read, not proof the playlist is unreadable.
                return offset == 0
                    ? new SpotifyPlaylistContents(playlist, [], page.Access, playlist.SnapshotId)
                    : new SpotifyPlaylistContents(playlist, items, SpotifyContentsAccess.Available, playlist.SnapshotId);
            }

            items.AddRange(page.Items);

            if (!page.HasMore || page.Items.Count == 0 || items.Count >= MaxItemsPerPlaylist)
                break;

            offset += page.Items.Count;
        }

        return new SpotifyPlaylistContents(
            playlist, items, SpotifyContentsAccess.Available, playlist.SnapshotId);
    }

    private sealed record CacheEntry(
        string SnapshotId,
        IReadOnlyList<SpotifyPlaylistItemDto> Items,
        SpotifyContentsAccess Access);
}
