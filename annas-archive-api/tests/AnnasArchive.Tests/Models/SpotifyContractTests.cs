using System.Text.Json;
using AnnasArchive.API.Models;
using FluentAssertions;

namespace AnnasArchive.Tests.Models;

public class SpotifyContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void PlaylistPage_Uses2026ItemsSummary_AndPreservesUnavailableContents()
    {
        var page = DeserializeFixture<SpotifyPlaylistsResponse>("playlists-page-2026.json");

        page.Total.Should().Be(3);
        page.Items.Should().HaveCount(3);

        var owned = page.Items[0];
        owned.ItemSummary.Should().NotBeNull();
        owned.ItemSummary!.Total.Should().Be(23);
        owned.Owner!.Id.Should().Be("test-owner");
        owned.SnapshotId.Should().Be("snapshot-owned");

        var collaborative = page.Items[1];
        collaborative.Collaborative.Should().BeTrue();
        collaborative.ItemSummary!.Total.Should().Be(8);

        var followed = page.Items[2];
        followed.ItemSummary.Should().BeNull(
            "Spotify omits items for playlists whose contents are unavailable");
    }

    [Fact]
    public void PlaylistItemsPage_Uses2026ItemWrapper_AndHandlesAllSupportedShapes()
    {
        var page = DeserializeFixture<SpotifyPlaylistItemsResponse>("playlist-items-page-2026.json");

        page.Total.Should().Be(4);
        page.Items.Should().HaveCount(4);

        var track = page.Items[0];
        track.Item!.Type.Should().Be("track");
        track.Item.Artists.Should().ContainSingle(a => a.Name == "Artist One");
        track.Item.Album!.Name.Should().Be("Album One");

        var episode = page.Items[1];
        episode.Item!.Type.Should().Be("episode");
        episode.Item.Artists.Should().BeNull();
        episode.Item.Album.Should().BeNull();

        var local = page.Items[2];
        local.IsLocal.Should().BeTrue();
        local.Item!.Id.Should().BeNull();
        local.Item.IsLocal.Should().BeTrue();

        page.Items[3].Item.Should().BeNull(
            "Spotify can retain an unavailable item wrapper with a null item");
    }

    [Fact]
    public void TokenResponse_PreservesReplacementRefreshTokenAndGrantedScope()
    {
        const string json = """
            {
              "access_token": "access-token",
              "token_type": "Bearer",
              "expires_in": 3600,
              "refresh_token": "replacement-refresh-token",
              "scope": "playlist-read-private playlist-modify-private"
            }
            """;

        var token = JsonSerializer.Deserialize<SpotifyTokenResponse>(json, JsonOptions);

        token.Should().NotBeNull();
        token!.RefreshToken.Should().Be("replacement-refresh-token");
        token.Scope.Should().Contain("playlist-read-private");
    }

    private static T DeserializeFixture<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spotify", fileName);
        File.Exists(path).Should().BeTrue($"contract fixture should exist at {path}");

        var result = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        result.Should().NotBeNull();
        return result!;
    }
}
