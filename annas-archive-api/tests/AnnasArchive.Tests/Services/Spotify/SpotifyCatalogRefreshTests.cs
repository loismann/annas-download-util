using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// The playlist list is cached for fifteen minutes, which is right for browsing and
/// wrong immediately after a change of ours.
///
/// Creating a playlist and then not seeing it reads as the change having failed —
/// so the caller can force a re-read. The cache still has to work by default, or
/// every page load re-fetches the whole library.
/// </summary>
public sealed class SpotifyCatalogRefreshTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"spotify-catalog-{Guid.NewGuid():N}");
    private readonly AppDatabase _database;
    private readonly CountingSpotify _spotify = new();
    private readonly SpotifyInventoryService _inventory;

    public SpotifyCatalogRefreshTests()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_directory, "app.db")
        }).Build();
        _database = new AppDatabase(configuration);

        _inventory = new SpotifyInventoryService(
            _spotify, new SpotifyInventoryStore(_database), new FixedUser(), TimeProvider.System);
    }

    [Fact]
    public async Task TheSecondReadComesFromTheCache()
    {
        await _inventory.GetPlaylistsAsync();
        await _inventory.GetPlaylistsAsync();

        _spotify.Calls.Should().Be(1, "browsing must not re-fetch the whole library each time");
    }

    [Fact]
    public async Task ForcingARefreshGoesBackToSpotify()
    {
        await _inventory.GetPlaylistsAsync();

        await _inventory.GetPlaylistsAsync(forceRefresh: true);

        _spotify.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AForcedRefreshShowsAPlaylistCreatedSinceTheCacheWasFilled()
    {
        // The whole point. Without this, a playlist you just made is invisible for
        // up to fifteen minutes, which looks exactly like the create having failed.
        await _inventory.GetPlaylistsAsync();
        _spotify.Playlists.Add(Playlist("new", "Morr Music Essentials"));

        var refreshed = await _inventory.GetPlaylistsAsync(forceRefresh: true);

        refreshed.Should().Contain(p => p.Name == "Morr Music Essentials");
    }

    [Fact]
    public async Task AForcedRefreshAlsoDropsAPlaylistThatIsGone()
    {
        // Unfollowing is the other half. A refresh that only ever adds would leave
        // a removed playlist sitting in the rail, clickable and empty.
        await _inventory.GetPlaylistsAsync();
        _spotify.Playlists.Clear();

        (await _inventory.GetPlaylistsAsync(forceRefresh: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task AForcedRefreshReplacesWhatTheNextCachedReadReturns()
    {
        // Forcing must write through, not just answer this one call — otherwise the
        // stale list comes straight back on the next render.
        await _inventory.GetPlaylistsAsync();
        _spotify.Playlists.Add(Playlist("new", "Morr Music Essentials"));
        await _inventory.GetPlaylistsAsync(forceRefresh: true);

        var cached = await _inventory.GetPlaylistsAsync();

        cached.Should().Contain(p => p.Name == "Morr Music Essentials");
        _spotify.Calls.Should().Be(2, "the write-through means no third call is needed");
    }

    private static SpotifyPlaylistDto Playlist(string id, string name) =>
        new(id, name, null, 0, null, SnapshotId: $"snap-{id}", OwnerId: "me", IsOwnedByUser: true);

    private sealed class CountingSpotify : ISpotifyService
    {
        public int Calls { get; private set; }
        public List<SpotifyPlaylistDto> Playlists { get; } = [Playlist("p1", "Existing")];

        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(Playlists.ToList());
        }

        // Everything else is out of scope here; throwing rather than returning empty
        // means a test that starts depending on one says so instead of quietly
        // asserting against a blank.
        public Task<SpotifySearchResultDto> SearchTracksAsync(string query, int limit = 10, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyTopItemsDto> GetTopItemsAsync(string kind = "tracks", string timeRange = "medium_term", int limit = 20, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(string name, string? description = null, bool isPublic = false, CancellationToken token = default) => throw new NotSupportedException();
        public Task AddTracksToPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveTracksFromPlaylistAsync(string playlistId, List<string> trackUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemovePlaylistsFromLibraryAsync(List<string> playlistUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> RemoveItemsAsync(string playlistId, IReadOnlyList<string> uris, string? snapshotId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> ReplaceItemsAsync(string playlistId, IReadOnlyList<string> orderedUris, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> ReorderItemsAsync(string playlistId, int rangeStart, int insertBefore, int rangeLength, string? snapshotId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task ChangePlaylistDetailsAsync(string playlistId, string? name = null, string? description = null, bool? isPublic = null, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FixedUser : ISpotifyCurrentUser
    {
        public string GetRequiredOwnerKey() => "owner";
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
