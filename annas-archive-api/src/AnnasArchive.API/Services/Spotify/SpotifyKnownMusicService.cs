using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyKnownMusicService
{
    Task<SpotifyKnownMusicReport> GetAsync(CancellationToken token = default);
    Task<SpotifyTopItemsDto> GetTopItemsAsync(
        string kind, string window, int limit = 20, CancellationToken token = default);
    Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentContextsAsync(
        CancellationToken token = default);
    SpotifyKnownMusicOverrideResult ApplyOverride(SpotifyKnownMusicOverrideRequest request);
}

/// <summary>
/// Orchestrates the pure known-music builder with the persisted inventory and the
/// Spotify signals that have different freshness windows.
/// </summary>
public sealed class SpotifyKnownMusicService(
    ISpotifyInventoryService inventory,
    ISpotifyInventoryStore store,
    ISpotifyService spotify,
    ISpotifyCurrentUser currentUser,
    TimeProvider timeProvider) : ISpotifyKnownMusicService
{
    private static readonly TimeSpan RecentFreshness = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TopFreshness = TimeSpan.FromHours(24);
    private static readonly string[] Windows = ["short_term", "medium_term", "long_term"];

    public async Task<SpotifyKnownMusicReport> GetAsync(CancellationToken token = default)
    {
        var ownerKey = currentUser.GetRequiredOwnerKey();
        var library = inventory.LoadCachedLibrary(ownerKey);

        var recent = store.GetSignal<List<SpotifyRecentTrackDto>>(ownerKey, "recent-tracks", RecentFreshness);
        if (recent == null)
        {
            recent = (await spotify.GetRecentlyPlayedTracksAsync(token)).ToList();
            store.SaveSignal(ownerKey, "recent-tracks", recent, timeProvider.GetUtcNow());
        }

        var topTracks = new List<SpotifyTopItemsDto>();
        var topArtists = new List<SpotifyTopItemsDto>();
        foreach (var window in Windows)
        {
            topTracks.Add(await GetTopAsync(ownerKey, "tracks", window, token));
            topArtists.Add(await GetTopAsync(ownerKey, "artists", window, token));
        }

        var combinedTracks = new SpotifyTopItemsDto(
            "tracks", "all_windows",
            topTracks.SelectMany(result => result.Items).DistinctBy(item => item.Id).ToList());
        var combinedArtists = new SpotifyTopItemsDto(
            "artists", "all_windows",
            topArtists.SelectMany(result => result.Items).DistinctBy(item => item.Id).ToList());

        var index = SpotifyKnownMusic.Build(
            library,
            combinedTracks,
            combinedArtists,
            recent.Select(item => item.Track).ToList());
        index = ApplyOverrides(index, store.GetKnownMusicOverrides(ownerKey));

        return new SpotifyKnownMusicReport(index, index.DescribeCoverage(), timeProvider.GetUtcNow());
    }

    public SpotifyKnownMusicOverrideResult ApplyOverride(SpotifyKnownMusicOverrideRequest request)
    {
        var ownerKey = currentUser.GetRequiredOwnerKey();
        var kind = string.Equals(request.Kind, "track", StringComparison.OrdinalIgnoreCase)
            ? "track"
            : "artist";
        var key = kind == "track"
            ? $"{SpotifyPlaylistResolver.Normalize(request.Artist)}|{SpotifyPlaylistResolver.Normalize(request.Name)}"
            : SpotifyPlaylistResolver.Normalize(request.Name);
        if (string.IsNullOrWhiteSpace(key) || key == "|")
            throw new ArgumentException("A name is required for a known-music override.");

        var now = timeProvider.GetUtcNow();
        store.SaveKnownMusicOverride(ownerKey,
            new SpotifyKnownMusicOverride(kind, key, request.Name, request.Known),
            now);
        return new SpotifyKnownMusicOverrideResult(kind, request.Name, request.Known, now);
    }

    public async Task<SpotifyTopItemsDto> GetTopItemsAsync(
        string kind, string window, int limit = 20, CancellationToken token = default)
    {
        var ownerKey = currentUser.GetRequiredOwnerKey();
        var cached = await GetTopAsync(ownerKey, kind, window, token);
        return cached with { Items = cached.Items.Take(Math.Clamp(limit, 1, 50)).ToList() };
    }

    public async Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentContextsAsync(
        CancellationToken token = default)
    {
        var ownerKey = currentUser.GetRequiredOwnerKey();
        var cached = store.GetSignal<List<SpotifyRecentPlaylistContextDto>>(
            ownerKey, "recent-contexts", RecentFreshness);
        if (cached != null)
            return cached;

        cached = (await spotify.GetRecentPlaylistContextsAsync(token)).ToList();
        store.SaveSignal(ownerKey, "recent-contexts", cached, timeProvider.GetUtcNow());
        return cached;
    }

    private async Task<SpotifyTopItemsDto> GetTopAsync(
        string ownerKey, string kind, string window, CancellationToken token)
    {
        var cacheKey = $"top:{kind}:{window}";
        var cached = store.GetSignal<SpotifyTopItemsDto>(ownerKey, cacheKey, TopFreshness);
        if (cached != null)
            return cached;

        var result = await spotify.GetTopItemsAsync(kind, window, 50, token);
        store.SaveSignal(ownerKey, cacheKey, result, timeProvider.GetUtcNow());
        return result;
    }

    private static SpotifyKnownMusicIndex ApplyOverrides(
        SpotifyKnownMusicIndex index,
        IReadOnlyList<SpotifyKnownMusicOverride> overrides)
    {
        var artists = index.ArtistKeys.ToHashSet(StringComparer.Ordinal);
        var tracks = index.TrackKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var value in overrides)
        {
            var target = value.Kind == "track" ? tracks : artists;
            if (value.IsKnown) target.Add(value.Key);
            else target.Remove(value.Key);
        }
        return index with { ArtistKeys = artists, TrackKeys = tracks, ExplicitOverrides = overrides.Count };
    }
}
