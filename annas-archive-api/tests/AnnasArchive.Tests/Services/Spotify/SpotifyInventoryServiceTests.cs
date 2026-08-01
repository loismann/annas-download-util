using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// Paging and the snapshot cache. Snapshot IDs change whenever a playlist's items
/// change, so an unchanged snapshot is proof a cached copy is still exact — which
/// is why this cache can be trusted where a TTL alone could not.
/// </summary>
public class SpotifyInventoryServiceTests
{
    [Fact]
    public async Task FollowsEveryPageUntilSpotifyStops()
    {
        var spotify = new PagingSpotify(totalItems: 120);
        var inventory = new SpotifyInventoryService(spotify);

        var contents = await inventory.GetContentsAsync(Playlist("p", "Big"));

        contents.Items.Should().HaveCount(120);
        contents.Items[0].Position.Should().Be(0);
        contents.Items[119].Position.Should().Be(119);
        spotify.Calls.Should().Be(3);
    }

    [Fact]
    public async Task ServesTheCachedCopyWhenTheSnapshotIsUnchanged()
    {
        var spotify = new PagingSpotify(totalItems: 10);
        var inventory = new SpotifyInventoryService(spotify);
        var playlist = Playlist("p", "Same", snapshot: "snap-1");

        await inventory.GetContentsAsync(playlist);
        await inventory.GetContentsAsync(playlist);

        spotify.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RefetchesWhenTheSnapshotChanges()
    {
        // The snapshot moving is Spotify telling us the contents moved.
        var spotify = new PagingSpotify(totalItems: 10);
        var inventory = new SpotifyInventoryService(spotify);

        await inventory.GetContentsAsync(Playlist("p", "Changing", snapshot: "snap-1"));
        await inventory.GetContentsAsync(Playlist("p", "Changing", snapshot: "snap-2"));

        spotify.Calls.Should().Be(2);
    }

    [Fact]
    public async Task NeverCachesAPlaylistWithNoSnapshotId()
    {
        // Without a snapshot there is nothing to prove a cached copy is current, so
        // serving one would be a guess.
        var spotify = new PagingSpotify(totalItems: 10);
        var inventory = new SpotifyInventoryService(spotify);
        var playlist = Playlist("p", "No Snapshot", snapshot: null);

        await inventory.GetContentsAsync(playlist);
        await inventory.GetContentsAsync(playlist);

        spotify.Calls.Should().Be(2);
    }

    [Fact]
    public async Task KeepsPlaylistCachesSeparate()
    {
        var spotify = new PagingSpotify(totalItems: 10);
        var inventory = new SpotifyInventoryService(spotify);

        await inventory.GetContentsAsync(Playlist("a", "A", snapshot: "snap"));
        await inventory.GetContentsAsync(Playlist("b", "B", snapshot: "snap"));

        spotify.Calls.Should().Be(2);
    }

    // ─── access outcomes ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReportsForbiddenWithoutInventingAnEmptyPlaylist()
    {
        var inventory = new SpotifyInventoryService(new FixedSpotify(SpotifyContentsAccess.Forbidden));

        var contents = await inventory.GetContentsAsync(Playlist("p", "Followed"));

        contents.Access.Should().Be(SpotifyContentsAccess.Forbidden);
        contents.IsReadable.Should().BeFalse();
        contents.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepsWhatItAlreadyReadWhenALaterPageFails()
    {
        // A first-page 403 means "not allowed". A later-page failure means we have a
        // partial read — throwing away the first 50 items would be worse.
        var spotify = new FailsAfterFirstPageSpotify();
        var inventory = new SpotifyInventoryService(spotify);

        var contents = await inventory.GetContentsAsync(Playlist("p", "Partial"));

        contents.Items.Should().HaveCount(50);
        contents.IsReadable.Should().BeTrue();
    }

    [Fact]
    public async Task RecordsAFailedPlaylistAsUnreadableRatherThanLosingTheWholeScan()
    {
        var inventory = new SpotifyInventoryService(new ThrowsForSpotify("bad"));

        var results = await inventory.GetAllContentsAsync(
            [Playlist("good", "Good"), Playlist("bad", "Bad")]);

        results.Should().HaveCount(2);
        results.Single(r => r.Playlist.Id == "bad").IsReadable.Should().BeFalse();
        results.Single(r => r.Playlist.Id == "good").IsReadable.Should().BeTrue();
    }

    [Fact]
    public async Task ReturnsScanResultsInTheOrderTheyWereRequested()
    {
        // The scan runs concurrently; callers still index by position.
        var inventory = new SpotifyInventoryService(new PagingSpotify(totalItems: 1));
        var playlists = Enumerable.Range(0, 10).Select(i => Playlist($"p{i}", $"P{i}")).ToList();

        var results = await inventory.GetAllContentsAsync(playlists);

        results.Select(r => r.Playlist.Id).Should().Equal(playlists.Select(p => p.Id));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static SpotifyPlaylistDto Playlist(string id, string name, string? snapshot = "snap-1") =>
        new(id, name, null, 0, null, IsOwnedByUser: true, SnapshotId: snapshot);

    private static SpotifyPlaylistItemDto Item(int position) =>
        new(position, SpotifyItemKind.Track, $"t{position}", $"Track {position}",
            $"spotify:track:{position}", "Artist", "Album", 1000, null, false, null);

    private abstract class SpotifyStub : ISpotifyService
    {
        public abstract Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default);

        public Task<List<SpotifyPlaylistDto>> GetUserPlaylistsAsync(CancellationToken token = default) =>
            Task.FromResult(new List<SpotifyPlaylistDto>());
        public Task<SpotifyPlaylistDto?> GetPlaylistAsync(string playlistId, CancellationToken token = default) =>
            Task.FromResult<SpotifyPlaylistDto?>(null);
        public Task<IReadOnlyList<SpotifyRecentPlaylistContextDto>> GetRecentPlaylistContextsAsync(
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<SpotifyRecentPlaylistContextDto>>([]);
        public Task<SpotifyTopItemsDto> GetTopItemsAsync(
            string kind = "tracks", string timeRange = "medium_term", int limit = 20,
            CancellationToken token = default) => Task.FromResult(new SpotifyTopItemsDto(kind, timeRange, []));
        public Task<SpotifySearchResultDto> SearchTracksAsync(
            string query, int limit = 10, CancellationToken token = default) =>
            Task.FromResult(new SpotifySearchResultDto([], 0));
        public Task<SpotifyPlaylistDto> CreatePlaylistAsync(
            string name, string? description = null, bool isPublic = false, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task AddTracksToPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task RemoveTracksFromPlaylistAsync(
            string playlistId, List<string> trackUris, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task RemovePlaylistsFromLibraryAsync(
            List<string> playlistUris, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class PagingSpotify(int totalItems) : SpotifyStub
    {
        private int _calls;
        public int Calls => _calls;

        public override Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls);
            var items = Enumerable.Range(offset, Math.Max(0, Math.Min(limit, totalItems - offset)))
                .Select(Item).ToList();

            return Task.FromResult(new SpotifyPlaylistItemsPageDto(
                playlistId, items, totalItems, offset, limit,
                HasMore: offset + items.Count < totalItems));
        }
    }

    private sealed class FixedSpotify(SpotifyContentsAccess access) : SpotifyStub
    {
        public override Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(new SpotifyPlaylistItemsPageDto(
                playlistId, [], 0, offset, limit, false, access));
    }

    private sealed class FailsAfterFirstPageSpotify : SpotifyStub
    {
        public override Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            Task.FromResult(offset == 0
                ? new SpotifyPlaylistItemsPageDto(
                    playlistId, Enumerable.Range(0, 50).Select(Item).ToList(), 120, 0, 50, HasMore: true)
                : new SpotifyPlaylistItemsPageDto(
                    playlistId, [], 0, offset, limit, false, SpotifyContentsAccess.Unavailable));
    }

    private sealed class ThrowsForSpotify(string failingPlaylistId) : SpotifyStub
    {
        public override Task<SpotifyPlaylistItemsPageDto> GetPlaylistItemsAsync(
            string playlistId, int offset = 0, int limit = 50, CancellationToken token = default) =>
            playlistId == failingPlaylistId
                ? throw new SpotifyApiException(System.Net.HttpStatusCode.InternalServerError, "boom", null, null)
                : Task.FromResult(new SpotifyPlaylistItemsPageDto(
                    playlistId, [Item(0)], 1, 0, 50, false));
    }
}
