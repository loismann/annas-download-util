using System.Net;
using System.Text;
using AnnasArchive.API.Data;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AnnasArchive.Tests.Services;

public class SpotifyAuthorizationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizationCodeFlow_ValidatesStateAndStoresStableAccountId()
    {
        var store = new MemoryConnectionStore();
        var clock = new MutableTimeProvider(Now);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/token" => JsonResponse($$"""
                {
                  "access_token": "initial-access-token",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "refresh_token": "initial-refresh-token",
                  "scope": "{{AllScopes}}"
                }
                """),
            "/v1/me" => JsonResponse("""
                {
                  "id": "legacy-user-id",
                  "account_id": "stable-account-id",
                  "display_name": "Paul"
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var service = CreateService(store, handler, clock);

        var authorizationUri = service.CreateAuthorizationUri("owner-1");
        var query = QueryHelpers.ParseQuery(authorizationUri.Query);
        query["scope"].ToString().Split(' ').Should().BeEquivalentTo(SpotifyAuthorizationService.RequiredScopes);
        query["redirect_uri"].ToString().Should().Be("https://app.example/api/spotify/oauth/callback");

        var completion = await service.CompleteAuthorizationAsync(
            query["state"].ToString(),
            "authorization-code",
            null);

        completion.Succeeded.Should().BeTrue();
        var connection = store.Get("owner-1");
        connection.Should().NotBeNull();
        connection!.AccountId.Should().Be("stable-account-id");
        connection.SpotifyUserId.Should().Be("legacy-user-id");
        connection.State.Should().Be(SpotifyConnectionState.Connected);

        var replay = await service.CompleteAuthorizationAsync(
            query["state"].ToString(),
            "authorization-code",
            null);
        replay.Succeeded.Should().BeFalse();
        replay.Error.Should().Be("state_mismatch");
    }

    [Fact]
    public void ConnectionStore_EncryptsTokensAtRest()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"spotify-store-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(tempDirectory, "app.db")
            })
            .Build();
        var database = new AppDatabase(configuration);
        var protection = new EphemeralDataProtectionProvider();
        var store = new SpotifyConnectionStore(database, protection);
        var record = Connection(accessToken: "plain-access-secret", refreshToken: "plain-refresh-secret");

        store.Save(record);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_state WHERE key LIKE 'spotify.connection.v1:%'";
        var persisted = command.ExecuteScalar() as string;
        persisted.Should().NotBeNullOrWhiteSpace();
        persisted.Should().NotContain("plain-access-secret");
        persisted.Should().NotContain("plain-refresh-secret");

        store.Get("owner-1").Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task Refresh_PersistsReplacementRefreshTokenBeforeReturningAccessToken()
    {
        var store = new MemoryConnectionStore();
        store.Save(Connection(
            accessToken: "expired-access",
            refreshToken: "old-refresh",
            accessTokenExpiresAt: Now.AddMinutes(-1)));
        var handler = new StubHttpMessageHandler(request => JsonResponse($$"""
            {
              "access_token": "new-access",
              "token_type": "Bearer",
              "expires_in": 3600,
              "refresh_token": "rotated-refresh",
              "scope": "{{AllScopes}}"
            }
            """));
        var service = CreateService(store, handler, new MutableTimeProvider(Now));

        var accessToken = await service.GetAccessTokenAsync();

        accessToken.Should().Be("new-access");
        store.Get("owner-1")!.RefreshToken.Should().Be("rotated-refresh");
        store.Get("owner-1")!.AccessToken.Should().Be("new-access");
    }

    [Fact]
    public async Task InvalidGrant_DiscardsTokensAndRequiresReauthorization()
    {
        var store = new MemoryConnectionStore();
        store.Save(Connection(accessTokenExpiresAt: Now.AddMinutes(-1)));
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """{ "error": "invalid_grant", "error_description": "Refresh token revoked" }""",
            HttpStatusCode.BadRequest));
        var service = CreateService(store, handler, new MutableTimeProvider(Now));

        var act = () => service.GetAccessTokenAsync();

        var exception = await act.Should().ThrowAsync<SpotifyConnectionException>();
        exception.Which.State.Should().Be(nameof(SpotifyConnectionState.ReauthorizationRequired));
        var stored = store.Get("owner-1")!;
        stored.State.Should().Be(SpotifyConnectionState.ReauthorizationRequired);
        stored.AccessToken.Should().BeEmpty();
        stored.RefreshToken.Should().BeEmpty();
    }

    [Fact]
    public void MissingScope_IsReportedWithoutExposingTokens()
    {
        var store = new MemoryConnectionStore();
        store.Save(Connection(scopes: ["playlist-read-private"]));
        var service = CreateService(store, new StubHttpMessageHandler(_ => JsonResponse("{}")), new MutableTimeProvider(Now));

        var status = service.GetStatus("owner-1");

        status.State.Should().Be(nameof(SpotifyConnectionState.ScopeLimited));
        status.MissingScopes.Should().Contain("playlist-read-collaborative");
        status.Warning.Should().Contain("missing capability");
    }

    [Fact]
    public void SixMonthAuthorizationDeadline_TransitionsToReauthorizationRequired()
    {
        var store = new MemoryConnectionStore();
        store.Save(Connection(authorizedAt: Now.AddMonths(-6)));
        var service = CreateService(store, new StubHttpMessageHandler(_ => JsonResponse("{}")), new MutableTimeProvider(Now));

        var status = service.GetStatus("owner-1");

        status.State.Should().Be(nameof(SpotifyConnectionState.ReauthorizationRequired));
        status.IsConnected.Should().BeFalse();
        store.Get("owner-1")!.RefreshToken.Should().BeEmpty();
    }

    private static string AllScopes => string.Join(' ', SpotifyAuthorizationService.RequiredScopes);

    private static SpotifyAuthorizationService CreateService(
        ISpotifyConnectionStore store,
        HttpMessageHandler handler,
        TimeProvider clock)
    {
        return new SpotifyAuthorizationService(
            new StubHttpClientFactory(handler),
            Options.Create(new SpotifyConfiguration
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RedirectUri = "https://app.example/api/spotify/oauth/callback",
                FrontendBaseUrl = "https://app.example"
            }),
            store,
            new SpotifyOAuthStateStore(clock),
            new StubCurrentUser("owner-1"),
            clock);
    }

    private static SpotifyConnectionRecord Connection(
        string accessToken = "access-token",
        string refreshToken = "refresh-token",
        DateTimeOffset? accessTokenExpiresAt = null,
        DateTimeOffset? authorizedAt = null,
        IReadOnlyList<string>? scopes = null) => new(
            "owner-1",
            "account-id",
            "spotify-user-id",
            "Paul",
            accessToken,
            refreshToken,
            accessTokenExpiresAt ?? Now.AddHours(1),
            scopes ?? SpotifyAuthorizationService.RequiredScopes,
            authorizedAt ?? Now,
            SpotifyConnectionState.Connected);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class MemoryConnectionStore : ISpotifyConnectionStore
    {
        private readonly Dictionary<string, SpotifyConnectionRecord> _connections = new(StringComparer.Ordinal);

        public SpotifyConnectionRecord? Get(string ownerKey) =>
            _connections.TryGetValue(ownerKey, out var connection) ? connection : null;

        public void Save(SpotifyConnectionRecord connection) =>
            _connections[connection.OwnerKey] = connection;

        public void Delete(string ownerKey) => _connections.Remove(ownerKey);
    }

    private sealed class StubCurrentUser(string ownerKey) : ISpotifyCurrentUser
    {
        public string GetRequiredOwnerKey() => ownerKey;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
