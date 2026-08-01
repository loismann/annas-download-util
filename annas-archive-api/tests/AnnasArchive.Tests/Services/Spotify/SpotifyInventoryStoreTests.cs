using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Spotify;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Spotify;

public sealed class SpotifyInventoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"spotify-store-{Guid.NewGuid():N}");
    private readonly AppDatabase _database;

    public SpotifyInventoryStoreTests()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_directory, "app.db")
        }).Build();
        _database = new AppDatabase(configuration);
    }

    [Fact]
    public void CompleteSnapshotSurvivesAServiceInstanceAndNeverCrossesAccounts()
    {
        var playlist = Playlist("p", "snap-1");
        var first = new SpotifyInventoryStore(_database);
        first.SaveMetadata("owner-a", [playlist], DateTimeOffset.UtcNow);
        first.SaveContents("owner-a", Contents(playlist, SpotifyContentsAccess.Available), DateTimeOffset.UtcNow);

        var afterReload = new SpotifyInventoryStore(_database);

        afterReload.GetCompleteContents("owner-a", playlist)!.Items.Should().ContainSingle();
        afterReload.GetCompleteContents("owner-b", playlist).Should().BeNull();
    }

    [Fact]
    public void PartialRefreshDoesNotEraseTheLastCompleteItemPayload()
    {
        var store = new SpotifyInventoryStore(_database);
        var original = Playlist("p", "snap-1");
        store.SaveMetadata("owner", [original], DateTimeOffset.UtcNow);
        store.SaveContents("owner", Contents(original, SpotifyContentsAccess.Available), DateTimeOffset.UtcNow);

        var changed = Playlist("p", "snap-2");
        store.SaveMetadata("owner", [changed], DateTimeOffset.UtcNow);
        store.SaveContents("owner", new SpotifyPlaylistContents(
            changed, [Item(0)], SpotifyContentsAccess.Partial, changed.SnapshotId), DateTimeOffset.UtcNow);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT items_snapshot_id, items_json FROM spotify_playlist_cache";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("snap-1");
        reader.GetString(1).Should().Contain("spotify:track:0");
        store.LoadLibrary("owner").Single().Access.Should().Be(SpotifyContentsAccess.Partial);
    }

    [Fact]
    public void PersistsInventoryProgress()
    {
        var store = new SpotifyInventoryStore(_database);
        var status = new SpotifyInventoryStatusDto(
            "job", SpotifyInventoryJobState.Running, 100, 42, 40, 1, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "42 of 100");

        store.SaveStatus("owner", status);

        var loaded = new SpotifyInventoryStore(_database).GetStatus("owner");
        loaded.JobId.Should().Be("job");
        loaded.ProcessedPlaylists.Should().Be(42);
        loaded.PartialPlaylists.Should().Be(1);
    }

    [Fact]
    public void MetadataListingAloneDoesNotClaimAFullInventoryCompleted()
    {
        var store = new SpotifyInventoryStore(_database);
        store.SaveMetadata("owner", [Playlist("p", "snap")], DateTimeOffset.UtcNow);

        store.GetLastInventoryAt("owner").Should().BeNull();

        var completed = DateTimeOffset.UtcNow;
        store.MarkFullInventory("owner", completed);
        store.GetLastInventoryAt("owner").Should().BeCloseTo(completed, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ClearOwnerRemovesOnlyThatAccountsSpotifyState()
    {
        var store = new SpotifyInventoryStore(_database);
        var playlist = Playlist("p", "snap");
        foreach (var owner in new[] { "owner-a", "owner-b" })
        {
            store.SaveMetadata(owner, [playlist], DateTimeOffset.UtcNow);
            store.SaveContents(owner, Contents(playlist, SpotifyContentsAccess.Available), DateTimeOffset.UtcNow);
            store.SaveSignal(owner, "recent", new List<string> { "track" }, DateTimeOffset.UtcNow);
            store.SaveKnownMusicOverride(owner,
                new SpotifyKnownMusicOverride("track", "song|artist", "Song", true), DateTimeOffset.UtcNow);
            store.SaveStatus(owner, new SpotifyInventoryStatusDto(
                "job", SpotifyInventoryJobState.Complete, 1, 1, 1, 0, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        store.ClearOwner("owner-a");

        store.GetMetadata("owner-a", TimeSpan.MaxValue).Should().BeNull();
        store.LoadLibrary("owner-a").Should().BeEmpty();
        store.GetSignal<List<string>>("owner-a", "recent", TimeSpan.MaxValue).Should().BeNull();
        store.GetKnownMusicOverrides("owner-a").Should().BeEmpty();
        store.GetStatus("owner-a").State.Should().Be(SpotifyInventoryJobState.NotStarted);
        store.GetCompleteContents("owner-b", playlist).Should().NotBeNull();
        store.GetSignal<List<string>>("owner-b", "recent", TimeSpan.MaxValue).Should().NotBeNull();
        store.GetKnownMusicOverrides("owner-b").Should().ContainSingle();
        store.GetStatus("owner-b").State.Should().Be(SpotifyInventoryJobState.Complete);
    }

    private static SpotifyPlaylistDto Playlist(string id, string snapshot) =>
        new(id, "Playlist", null, 1, null, SnapshotId: snapshot, IsOwnedByUser: true);

    private static SpotifyPlaylistContents Contents(SpotifyPlaylistDto playlist, SpotifyContentsAccess access) =>
        new(playlist, [Item(0)], access, playlist.SnapshotId);

    private static SpotifyPlaylistItemDto Item(int position) =>
        new(position, SpotifyItemKind.Track, "track", "Song", "spotify:track:0",
            "Artist", "Album", 1000, null, false, null, "USRC10000001");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
