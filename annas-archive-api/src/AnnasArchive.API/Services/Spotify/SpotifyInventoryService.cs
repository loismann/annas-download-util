using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyInventoryService
{
    Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync(
        bool forceRefresh = false, CancellationToken token = default);
    Task<SpotifyPlaylistContents> GetContentsAsync(
        SpotifyPlaylistDto playlist, CancellationToken token = default);
    Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
        IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default);
    Task<IReadOnlyList<SpotifyPlaylistContents>> RefreshForOwnerAsync(
        string ownerKey,
        Action<int, int, SpotifyPlaylistContents>? progress = null,
        CancellationToken token = default);
    IReadOnlyList<SpotifyPlaylistContents> LoadCachedLibrary(string ownerKey);
}

/// <summary>
/// Fetches complete playlist contents and stores only proven-complete snapshots.
/// Production uses the tenant-keyed SQLite store; the one-argument constructor is
/// retained as an isolated in-memory harness for unit tests.
/// </summary>
public sealed class SpotifyInventoryService : ISpotifyInventoryService
{
    private static readonly TimeSpan MetadataFreshness = TimeSpan.FromMinutes(15);
    private const int ScanConcurrency = 2;
    private const int PageSize = 50;
    private const int MaxItemsPerPlaylist = 10_000;

    private readonly ISpotifyService _spotify;
    private readonly ISpotifyInventoryStore? _store;
    private readonly ISpotifyCurrentUser? _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly LruCache<string, MemoryEntry> _memoryCache = new(500);

    public SpotifyInventoryService(ISpotifyService spotify)
    {
        _spotify = spotify;
        _timeProvider = TimeProvider.System;
    }

    public SpotifyInventoryService(
        ISpotifyService spotify,
        ISpotifyInventoryStore store,
        ISpotifyCurrentUser currentUser,
        TimeProvider timeProvider)
    {
        _spotify = spotify;
        _store = store;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync(
        bool forceRefresh = false, CancellationToken token = default)
    {
        if (_store == null || _currentUser == null)
            return await _spotify.GetUserPlaylistsAsync(token);

        var ownerKey = _currentUser.GetRequiredOwnerKey();
        if (!forceRefresh && _store.GetMetadata(ownerKey, MetadataFreshness) is { } cached)
            return cached;

        var playlists = await _spotify.GetUserPlaylistsAsync(token);
        var now = _timeProvider.GetUtcNow();
        var inventoried = playlists.Select(playlist => playlist with { InventoryAt = now }).ToList();
        _store.SaveMetadata(ownerKey, inventoried, now);
        return inventoried;
    }

    public Task<SpotifyPlaylistContents> GetContentsAsync(
        SpotifyPlaylistDto playlist, CancellationToken token = default)
    {
        var ownerKey = _store != null ? _currentUser!.GetRequiredOwnerKey() : null;
        return GetContentsCoreAsync(ownerKey, playlist, useOwnerApi: false, token);
    }

    public async Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(
        IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default)
    {
        var ownerKey = _store != null ? _currentUser!.GetRequiredOwnerKey() : null;
        return await GetAllContentsCoreAsync(ownerKey, playlists, useOwnerApi: false, progress: null, token);
    }

    public async Task<IReadOnlyList<SpotifyPlaylistContents>> RefreshForOwnerAsync(
        string ownerKey,
        Action<int, int, SpotifyPlaylistContents>? progress = null,
        CancellationToken token = default)
    {
        if (_store == null)
            throw new InvalidOperationException("Persistent Spotify inventory storage is not configured.");

        var playlists = await _spotify.GetUserPlaylistsForOwnerAsync(ownerKey, token);
        token.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var inventoried = playlists.Select(playlist => playlist with { InventoryAt = now }).ToList();
        _store.SaveMetadata(ownerKey, inventoried, now);
        return await GetAllContentsCoreAsync(ownerKey, inventoried, useOwnerApi: true, progress, token);
    }

    public IReadOnlyList<SpotifyPlaylistContents> LoadCachedLibrary(string ownerKey) =>
        _store?.LoadLibrary(ownerKey) ?? [];

    private async Task<SpotifyPlaylistContents> GetContentsCoreAsync(
        string? ownerKey,
        SpotifyPlaylistDto playlist,
        bool useOwnerApi,
        CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(ownerKey) &&
            _store!.GetCompleteContents(ownerKey, playlist) is { } persisted)
            return persisted;

        if (ownerKey == null && !string.IsNullOrWhiteSpace(playlist.SnapshotId) &&
            _memoryCache.TryGetValue(playlist.Id, out var memory) &&
            memory!.SnapshotId == playlist.SnapshotId)
        {
            return new SpotifyPlaylistContents(
                playlist, memory.Items, SpotifyContentsAccess.Available, memory.SnapshotId);
        }

        var contents = await FetchAllPagesAsync(ownerKey, playlist, useOwnerApi, token);
        token.ThrowIfCancellationRequested();

        if (ownerKey != null)
        {
            _store!.SaveContents(ownerKey, contents, _timeProvider.GetUtcNow());
        }
        else if (contents.Access == SpotifyContentsAccess.Available &&
                 !string.IsNullOrWhiteSpace(playlist.SnapshotId))
        {
            _memoryCache.Set(playlist.Id, new MemoryEntry(playlist.SnapshotId!, contents.Items));
        }

        return contents;
    }

    private async Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsCoreAsync(
        string? ownerKey,
        IReadOnlyList<SpotifyPlaylistDto> playlists,
        bool useOwnerApi,
        Action<int, int, SpotifyPlaylistContents>? progress,
        CancellationToken token)
    {
        var results = new SpotifyPlaylistContents[playlists.Count];
        using var gate = new SemaphoreSlim(ScanConcurrency);
        var processed = 0;

        var work = playlists.Select(async (playlist, index) =>
        {
            await gate.WaitAsync(token);
            SpotifyPlaylistContents result;
            try
            {
                result = await GetContentsCoreAsync(ownerKey, playlist, useOwnerApi, token);
            }
            catch (Exception ex) when (ex is SpotifyApiException or SpotifyConnectionException or HttpRequestException)
            {
                Log.Warning(ex, "[Spotify] Could not read playlist {PlaylistId}", playlist.Id);
                result = new SpotifyPlaylistContents(
                    playlist, [], SpotifyContentsAccess.Unavailable, playlist.SnapshotId);
                if (ownerKey != null)
                {
                    token.ThrowIfCancellationRequested();
                    _store!.SaveContents(ownerKey, result, _timeProvider.GetUtcNow());
                }
            }
            finally
            {
                gate.Release();
            }

            results[index] = result;
            var completed = Interlocked.Increment(ref processed);
            progress?.Invoke(completed, playlists.Count, result);
        });

        await Task.WhenAll(work);
        return results;
    }

    private async Task<SpotifyPlaylistContents> FetchAllPagesAsync(
        string? ownerKey,
        SpotifyPlaylistDto playlist,
        bool useOwnerApi,
        CancellationToken token)
    {
        var items = new List<SpotifyPlaylistItemDto>();
        var offset = 0;
        int? expectedTotal = null;

        while (true)
        {
            var page = useOwnerApi
                ? await _spotify.GetPlaylistItemsForOwnerAsync(
                    ownerKey!, playlist.Id, offset, PageSize, token)
                : await _spotify.GetPlaylistItemsAsync(playlist.Id, offset, PageSize, token);

            if (page.Access != SpotifyContentsAccess.Available)
            {
                return offset == 0
                    ? new SpotifyPlaylistContents(playlist, [], page.Access, playlist.SnapshotId)
                    : new SpotifyPlaylistContents(
                        playlist, items, SpotifyContentsAccess.Partial, playlist.SnapshotId);
            }

            expectedTotal ??= page.Total;
            items.AddRange(page.Items);

            if (items.Count >= MaxItemsPerPlaylist && page.HasMore)
            {
                return new SpotifyPlaylistContents(
                    playlist, items, SpotifyContentsAccess.Partial, playlist.SnapshotId);
            }

            if (page.Items.Count == 0 && page.HasMore)
                return new SpotifyPlaylistContents(
                    playlist, items, SpotifyContentsAccess.Partial, playlist.SnapshotId);

            if (!page.HasMore)
                break;

            offset += page.Items.Count;
        }

        var access = expectedTotal.HasValue && items.Count == expectedTotal.Value
            ? SpotifyContentsAccess.Available
            : SpotifyContentsAccess.Partial;
        return new SpotifyPlaylistContents(playlist, items, access, playlist.SnapshotId);
    }

    private sealed record MemoryEntry(
        string SnapshotId,
        IReadOnlyList<SpotifyPlaylistItemDto> Items);
}
