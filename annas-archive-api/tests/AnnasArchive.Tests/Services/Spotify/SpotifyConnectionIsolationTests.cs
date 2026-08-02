using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services.Spotify;

/// <summary>
/// One household member cannot reach another's Spotify connection.
///
/// This mattered less when the feature was admin-only and one person used it. Now
/// that every signed-in person can connect their own account, the store is holding
/// several people's live OAuth refresh tokens at once, and the only thing keeping
/// them apart is the owner key. So it is pinned rather than assumed.
///
/// Being an admin is deliberately absent from these tests: the store has no notion
/// of a role, and it must stay that way — an admin reading someone else's music
/// account is exactly the failure this guards.
/// </summary>
public sealed class SpotifyConnectionIsolationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"spotify-connection-{Guid.NewGuid():N}");
    private readonly SpotifyConnectionStore _store;

    public SpotifyConnectionIsolationTests()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_directory, "app.db")
        }).Build();

        _store = new SpotifyConnectionStore(
            new AppDatabase(configuration),
            DataProtectionProvider.Create(new DirectoryInfo(_directory)));
    }

    [Fact]
    public void OneOwnerNeverSeesAnother()
    {
        _store.Save(Connection("paul", "spotify-paul"));
        _store.Save(Connection("mom", "spotify-mom"));

        _store.Get("paul")!.SpotifyUserId.Should().Be("spotify-paul");
        _store.Get("mom")!.SpotifyUserId.Should().Be("spotify-mom");
    }

    [Fact]
    public void SavingASecondAccountDoesNotOverwriteTheFirst()
    {
        // Both people connect. If the storage key ignored the owner, the second
        // authorization would silently take over the first person's account — and
        // it would look like it worked, right up until Paul's playlists were gone.
        _store.Save(Connection("paul", "spotify-paul"));
        _store.Save(Connection("mom", "spotify-mom"));

        _store.Get("paul")!.RefreshToken.Should().Be("refresh-for-paul");
    }

    [Fact]
    public void AnOwnerWithNoConnectionGetsNothingRatherThanSomeoneElses()
    {
        _store.Save(Connection("paul", "spotify-paul"));

        _store.Get("dad").Should().BeNull();
    }

    [Fact]
    public void DisconnectingRemovesOnlyThatPersonsConnection()
    {
        _store.Save(Connection("paul", "spotify-paul"));
        _store.Save(Connection("mom", "spotify-mom"));

        _store.Delete("mom");

        _store.Get("mom").Should().BeNull();
        _store.Get("paul").Should().NotBeNull("disconnecting is not a household-wide sign-out");
    }

    [Fact]
    public void OwnerKeysThatDifferOnlyByCaseAreDifferentPeople()
    {
        // The owner key is an opaque credential, not a display name. Folding case
        // would merge two accounts that the login system treats as distinct.
        _store.Save(Connection("Paul", "spotify-upper"));
        _store.Save(Connection("paul", "spotify-lower"));

        _store.Get("Paul")!.SpotifyUserId.Should().Be("spotify-upper");
        _store.Get("paul")!.SpotifyUserId.Should().Be("spotify-lower");
    }

    private static SpotifyConnectionRecord Connection(string ownerKey, string spotifyUserId) =>
        new(
            OwnerKey: ownerKey,
            AccountId: $"account-{ownerKey}",
            SpotifyUserId: spotifyUserId,
            DisplayName: spotifyUserId,
            AccessToken: $"access-for-{ownerKey}",
            RefreshToken: $"refresh-for-{ownerKey}",
            AccessTokenExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            GrantedScopes: ["user-read-private"],
            AuthorizedAt: DateTimeOffset.UtcNow,
            State: SpotifyConnectionState.Connected);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
