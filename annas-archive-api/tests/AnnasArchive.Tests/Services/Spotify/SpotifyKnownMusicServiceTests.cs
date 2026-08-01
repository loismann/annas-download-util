using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Spotify;

public sealed class SpotifyKnownMusicServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"known-music-{Guid.NewGuid():N}");
    private readonly SpotifyInventoryStore _store;

    public SpotifyKnownMusicServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_directory, "app.db")
        }).Build();
        _store = new SpotifyInventoryStore(new AppDatabase(configuration));
    }

    [Fact]
    public async Task AggregatesAllTopWindowsAndCachesSignalsAtTheirOwnFreshness()
    {
        var spotify = new SignalSpotify();
        var service = new SpotifyKnownMusicService(
            new CachedInventory(), _store, spotify, new CurrentUser(), TimeProvider.System);

        var first = await service.GetAsync();
        var second = await service.GetAsync();

        spotify.TopCalls.Should().Be(6);
        spotify.RecentCalls.Should().Be(1);
        first.Index.ArtistKeys.Should().Contain([
            "libraryartist", "recentartist", "topartist", "toptrackartist"
        ]);
        second.Index.TrackKeys.Should().Contain("recentartist|recentsong");
        first.Index.IncludesRecentHistory.Should().BeTrue();
        first.Index.IncludesTopItems.Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitUnfamiliarOverrideWinsOverObservedLibraryData()
    {
        var service = new SpotifyKnownMusicService(
            new CachedInventory(), _store, new SignalSpotify(), new CurrentUser(), TimeProvider.System);
        service.ApplyOverride(new SpotifyKnownMusicOverrideRequest(
            "artist", "Library Artist", Known: false));

        var report = await service.GetAsync();

        report.Index.ArtistKeys.Should().NotContain("libraryartist");
        report.Index.ExplicitOverrides.Should().Be(1);
    }

    private sealed class CurrentUser : ISpotifyCurrentUser
    {
        public string GetRequiredOwnerKey() => "owner";
    }

    private sealed class CachedInventory : ISpotifyInventoryService
    {
        private static readonly SpotifyPlaylistDto Playlist =
            new("p", "P", null, 1, null, SnapshotId: "snap", IsOwnedByUser: true);
        private static readonly IReadOnlyList<SpotifyPlaylistContents> Library =
        [
            new(Playlist,
                [new SpotifyPlaylistItemDto(0, SpotifyItemKind.Track, "library", "Library Song",
                    "spotify:track:library", "Library Artist", "Album", 1000, null, false, null)],
                SpotifyContentsAccess.Available, "snap")
        ];

        public IReadOnlyList<SpotifyPlaylistContents> LoadCachedLibrary(string ownerKey) => Library;
        public Task<IReadOnlyList<SpotifyPlaylistDto>> GetPlaylistsAsync(bool forceRefresh = false, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SpotifyPlaylistDto>>([Playlist]);
        public Task<SpotifyPlaylistContents> GetContentsAsync(SpotifyPlaylistDto playlist, CancellationToken token = default) =>
            Task.FromResult(Library[0]);
        public Task<IReadOnlyList<SpotifyPlaylistContents>> GetAllContentsAsync(IReadOnlyList<SpotifyPlaylistDto> playlists, CancellationToken token = default) =>
            Task.FromResult(Library);
        public Task<IReadOnlyList<SpotifyPlaylistContents>> RefreshForOwnerAsync(string ownerKey, Action<int, int, SpotifyPlaylistContents>? progress = null, CancellationToken token = default) =>
            Task.FromResult(Library);
    }

    private sealed class SignalSpotify : ISpotifyService
    {
        public int TopCalls { get; private set; }
        public int RecentCalls { get; private set; }

        public Task<IReadOnlyList<SpotifyRecentTrackDto>> GetRecentlyPlayedTracksAsync(CancellationToken token = default)
        {
            RecentCalls++;
            return Task.FromResult<IReadOnlyList<SpotifyRecentTrackDto>>([
                new(new SpotifyPlaylistItemDto(0, SpotifyItemKind.Track, "recent", "Recent Song",
                    "spotify:track:recent", "Recent Artist", "Album", 1000, null, false, null),
                    DateTimeOffset.UtcNow, "playlist", "spotify:playlist:p")
            ]);
        }

        public Task<SpotifyTopItemsDto> GetTopItemsAsync(string kind = "tracks", string timeRange = "medium_term", int limit = 20, CancellationToken token = default)
        {
            TopCalls++;
            return Task.FromResult(kind == "artists"
                ? new SpotifyTopItemsDto(kind, timeRange, [new("artist", "Top Artist", null, null, 1)])
                : new SpotifyTopItemsDto(kind, timeRange, [new("track", "Top Song", "Top Track Artist", null, 1)]));
        }

        public Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 10, CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default) => throw new NotSupportedException();
        public Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemovePlaylistsFromLibraryAsync(List<string> playlistUris, CancellationToken token = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
