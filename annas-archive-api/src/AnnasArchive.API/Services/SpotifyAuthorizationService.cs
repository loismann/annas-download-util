using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

namespace AnnasArchive.API.Services;

public interface ISpotifyAuthorizationService
{
    Uri CreateAuthorizationUri(string ownerKey, bool forceDialog = false);
    Task<SpotifyOAuthCompletion> CompleteAuthorizationAsync(
        string? state,
        string? code,
        string? error,
        CancellationToken token = default);
    SpotifyConnectionStatusDto GetStatus(string ownerKey);
    void Disconnect(string ownerKey);
}

public interface ISpotifyAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default);
    Task RecordSuccessfulCallAsync(CancellationToken token = default);
    Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default);
    Task RecordUnavailableAsync(string message, CancellationToken token = default);
}

public sealed class SpotifyAuthorizationService : ISpotifyAuthorizationService, ISpotifyAccessTokenProvider
{
    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "playlist-read-private",
        "playlist-read-collaborative",
        "playlist-modify-private",
        "playlist-modify-public",
        "user-read-private",
        "user-top-read",
        "user-read-recently-played"
    ];

    private static readonly TimeSpan AccessTokenSkew = TimeSpan.FromMinutes(5);
    private const string AuthorizationUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string ProfileUrl = "https://api.spotify.com/v1/me";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpotifyConfiguration _config;
    private readonly ISpotifyConnectionStore _connections;
    private readonly ISpotifyOAuthStateStore _states;
    private readonly ISpotifyCurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    public SpotifyAuthorizationService(
        IHttpClientFactory httpClientFactory,
        IOptions<SpotifyConfiguration> config,
        ISpotifyConnectionStore connections,
        ISpotifyOAuthStateStore states,
        ISpotifyCurrentUser currentUser,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _connections = connections;
        _states = states;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public Uri CreateAuthorizationUri(string ownerKey, bool forceDialog = false)
    {
        ValidateConfiguration();
        var state = _states.Create(ownerKey);
        var query = new QueryBuilder
        {
            { "client_id", _config.ClientId },
            { "response_type", "code" },
            { "redirect_uri", _config.RedirectUri },
            { "state", state },
            { "scope", string.Join(' ', RequiredScopes) }
        };

        if (forceDialog)
            query.Add("show_dialog", "true");

        return new Uri(AuthorizationUrl + query.ToQueryString());
    }

    public async Task<SpotifyOAuthCompletion> CompleteAuthorizationAsync(
        string? state,
        string? code,
        string? error,
        CancellationToken token = default)
    {
        ValidateConfiguration();

        if (string.IsNullOrWhiteSpace(state) || !_states.TryConsume(state, out var ownerKey))
            return new SpotifyOAuthCompletion(string.Empty, false, "state_mismatch");

        if (!string.IsNullOrWhiteSpace(error))
            return new SpotifyOAuthCompletion(ownerKey, false, error);

        if (string.IsNullOrWhiteSpace(code))
            return new SpotifyOAuthCompletion(ownerKey, false, "missing_code");

        var now = _timeProvider.GetUtcNow();
        var tokenResponse = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _config.RedirectUri
        }, token);

        if (string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            throw new SpotifyConnectionException(
                "Spotify did not return a refresh token. Please authorize again.",
                nameof(SpotifyConnectionState.ReauthorizationRequired),
                HttpStatusCode.BadGateway);

        var grantedScopes = NormalizeScopes(tokenResponse.Scope);
        var profile = await RequestProfileAsync(tokenResponse.AccessToken, token);
        var accountId = string.IsNullOrWhiteSpace(profile.AccountId) ? profile.Id : profile.AccountId;

        var connection = new SpotifyConnectionRecord(
            ownerKey,
            accountId,
            profile.Id,
            profile.DisplayName,
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            now.AddSeconds(tokenResponse.ExpiresIn),
            grantedScopes,
            now,
            MissingScopes(grantedScopes).Count == 0
                ? SpotifyConnectionState.Connected
                : SpotifyConnectionState.ScopeLimited,
            LastSuccessfulCallAt: now);

        _connections.Save(connection);
        Log.Information(
            "[Spotify] Connected account {AccountId} with {ScopeCount} granted scopes",
            accountId,
            grantedScopes.Count);

        return new SpotifyOAuthCompletion(ownerKey, true);
    }

    public SpotifyConnectionStatusDto GetStatus(string ownerKey)
    {
        var connection = _connections.Get(ownerKey);
        if (connection == null)
        {
            return new SpotifyConnectionStatusDto(
                "Disconnected",
                false,
                null,
                null,
                null,
                [],
                RequiredScopes,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        connection = NormalizeState(connection);
        var missingScopes = MissingScopes(connection.GrantedScopes);
        var dueAt = connection.AuthorizedAt.AddMonths(6);
        var remaining = dueAt - _timeProvider.GetUtcNow();
        var days = Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));

        return new SpotifyConnectionStatusDto(
            connection.State.ToString(),
            connection.State != SpotifyConnectionState.ReauthorizationRequired,
            connection.AccountId,
            connection.SpotifyUserId,
            connection.DisplayName,
            connection.GrantedScopes,
            missingScopes,
            connection.AuthorizedAt,
            dueAt,
            days,
            connection.LastSuccessfulCallAt,
            connection.RateLimitedUntil,
            BuildWarning(connection, missingScopes, days),
            connection.LastError);
    }

    public void Disconnect(string ownerKey)
    {
        _connections.Delete(ownerKey);
        Log.Information("[Spotify] Local Spotify connection removed for owner {OwnerKeyHash}",
            ownerKey.GetHashCode(StringComparison.Ordinal));
    }

    public async Task<string> GetAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken token = default)
    {
        var ownerKey = _currentUser.GetRequiredOwnerKey();
        var connection = _connections.Get(ownerKey)
            ?? throw new SpotifyConnectionException(
                "Spotify is not connected.",
                "Disconnected");

        connection = NormalizeState(connection);
        var now = _timeProvider.GetUtcNow();

        if (connection.State == SpotifyConnectionState.ReauthorizationRequired)
        {
            throw new SpotifyConnectionException(
                "Spotify authorization has expired or was revoked. Reauthorization is required.",
                connection.State.ToString());
        }

        if (connection.State == SpotifyConnectionState.RateLimited &&
            connection.RateLimitedUntil is { } retryAt && retryAt > now)
        {
            throw new SpotifyConnectionException(
                "Spotify is rate limiting requests. Try again after the displayed retry time.",
                connection.State.ToString(),
                HttpStatusCode.TooManyRequests,
                retryAt - now);
        }

        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(connection.AccessToken) &&
            connection.AccessTokenExpiresAt > now.Add(AccessTokenSkew))
        {
            return connection.AccessToken;
        }

        return await RefreshAccessTokenAsync(ownerKey, forceRefresh, token);
    }

    public Task RecordSuccessfulCallAsync(CancellationToken token = default) =>
        UpdateCurrentConnectionAsync(connection =>
        {
            var state = MissingScopes(connection.GrantedScopes).Count == 0
                ? SpotifyConnectionState.Connected
                : SpotifyConnectionState.ScopeLimited;
            return connection with
            {
                State = state,
                LastSuccessfulCallAt = _timeProvider.GetUtcNow(),
                RateLimitedUntil = null,
                LastError = null
            };
        }, token);

    public Task RecordApiFailureAsync(SpotifyApiException exception, CancellationToken token = default) =>
        UpdateCurrentConnectionAsync(connection =>
        {
            var state = exception.SpotifyStatusCode switch
            {
                HttpStatusCode.Unauthorized => SpotifyConnectionState.ReauthorizationRequired,
                HttpStatusCode.TooManyRequests when exception.IsQuotaExceeded => SpotifyConnectionState.QuotaExceeded,
                HttpStatusCode.TooManyRequests => SpotifyConnectionState.RateLimited,
                _ when (int)exception.SpotifyStatusCode >= 500 => SpotifyConnectionState.SpotifyUnavailable,
                _ => connection.State
            };

            return connection with
            {
                State = state,
                RateLimitedUntil = state == SpotifyConnectionState.RateLimited && exception.RetryAfter is { } delay
                    ? _timeProvider.GetUtcNow().Add(delay)
                    : connection.RateLimitedUntil,
                LastError = exception.SpotifyMessage ?? exception.Message
            };
        }, token);

    public Task RecordUnavailableAsync(string message, CancellationToken token = default) =>
        UpdateCurrentConnectionAsync(connection => connection with
        {
            State = SpotifyConnectionState.SpotifyUnavailable,
            LastError = message
        }, token);

    private async Task<string> RefreshAccessTokenAsync(
        string ownerKey,
        bool forceRefresh,
        CancellationToken token)
    {
        var refreshLock = _refreshLocks.GetOrAdd(ownerKey, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(token);
        try
        {
            var connection = _connections.Get(ownerKey)
                ?? throw new SpotifyConnectionException("Spotify is not connected.", "Disconnected");
            var now = _timeProvider.GetUtcNow();

            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(connection.AccessToken) &&
                connection.AccessTokenExpiresAt > now.Add(AccessTokenSkew))
            {
                return connection.AccessToken;
            }

            if (connection.AuthorizedAt.AddMonths(6) <= now || string.IsNullOrWhiteSpace(connection.RefreshToken))
            {
                _connections.Save(connection with
                {
                    State = SpotifyConnectionState.ReauthorizationRequired,
                    AccessToken = string.Empty,
                    RefreshToken = string.Empty,
                    LastError = "Spotify authorization is no longer valid."
                });
                throw new SpotifyConnectionException(
                    "Spotify authorization is more than six months old. Reauthorization is required.",
                    nameof(SpotifyConnectionState.ReauthorizationRequired));
            }

            SpotifyTokenResponse refreshed;
            try
            {
                refreshed = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = connection.RefreshToken
                }, token);
            }
            catch (SpotifyConnectionException ex) when (ex.State == nameof(SpotifyConnectionState.ReauthorizationRequired))
            {
                _connections.Save(connection with
                {
                    State = SpotifyConnectionState.ReauthorizationRequired,
                    AccessToken = string.Empty,
                    RefreshToken = string.Empty,
                    LastError = ex.Message
                });
                throw;
            }

            var scopes = string.IsNullOrWhiteSpace(refreshed.Scope)
                ? connection.GrantedScopes
                : NormalizeScopes(refreshed.Scope);
            var replacementRefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                ? connection.RefreshToken
                : refreshed.RefreshToken;

            var updated = connection with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = replacementRefreshToken,
                AccessTokenExpiresAt = now.AddSeconds(refreshed.ExpiresIn),
                GrantedScopes = scopes,
                State = MissingScopes(scopes).Count == 0
                    ? SpotifyConnectionState.Connected
                    : SpotifyConnectionState.ScopeLimited,
                RateLimitedUntil = null,
                LastError = null
            };

            // SQLite upsert is atomic; persist a replacement refresh token before
            // returning the new access token to any Spotify request.
            _connections.Save(updated);
            return updated.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<SpotifyTokenResponse> RequestTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken token)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_config.ClientId}:{_config.ClientSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(form);

        var client = _httpClientFactory.CreateClient("SpotifyAccounts");
        using var response = await client.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            var tokenError = TryDeserialize<SpotifyTokenErrorResponse>(content);
            if (string.Equals(tokenError?.Error, "invalid_grant", StringComparison.Ordinal))
            {
                throw new SpotifyConnectionException(
                    "Spotify authorization expired or was revoked. Reauthorization is required.",
                    nameof(SpotifyConnectionState.ReauthorizationRequired));
            }

            Log.Warning("[Spotify] OAuth token request failed with {StatusCode}: {Error}",
                response.StatusCode, tokenError?.Error ?? "unknown_error");
            throw new SpotifyConnectionException(
                "Spotify could not complete the authorization token request.",
                nameof(SpotifyConnectionState.SpotifyUnavailable),
                HttpStatusCode.BadGateway);
        }

        return TryDeserialize<SpotifyTokenResponse>(content)
            ?? throw new SpotifyConnectionException(
                "Spotify returned an invalid token response.",
                nameof(SpotifyConnectionState.SpotifyUnavailable),
                HttpStatusCode.BadGateway);
    }

    private async Task<SpotifyUserProfile> RequestProfileAsync(string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var client = _httpClientFactory.CreateClient("SpotifyAccounts");
        using var response = await client.SendAsync(request, token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Spotify] Profile request during OAuth failed with {StatusCode}", response.StatusCode);
            throw new SpotifyConnectionException(
                "Spotify connected, but the account profile could not be verified.",
                nameof(SpotifyConnectionState.SpotifyUnavailable),
                HttpStatusCode.BadGateway);
        }

        var content = await response.Content.ReadAsStringAsync(token);
        return TryDeserialize<SpotifyUserProfile>(content)
            ?? throw new SpotifyConnectionException(
                "Spotify returned an invalid account profile.",
                nameof(SpotifyConnectionState.SpotifyUnavailable),
                HttpStatusCode.BadGateway);
    }

    private Task UpdateCurrentConnectionAsync(
        Func<SpotifyConnectionRecord, SpotifyConnectionRecord> update,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ownerKey = _currentUser.GetRequiredOwnerKey();
        var refreshLock = _refreshLocks.GetOrAdd(ownerKey, static _ => new SemaphoreSlim(1, 1));
        return UpdateUnderLockAsync(ownerKey, refreshLock, update, token);
    }

    private async Task UpdateUnderLockAsync(
        string ownerKey,
        SemaphoreSlim refreshLock,
        Func<SpotifyConnectionRecord, SpotifyConnectionRecord> update,
        CancellationToken token)
    {
        await refreshLock.WaitAsync(token);
        try
        {
            var connection = _connections.Get(ownerKey);
            if (connection != null)
                _connections.Save(update(connection));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private SpotifyConnectionRecord NormalizeState(SpotifyConnectionRecord connection)
    {
        var now = _timeProvider.GetUtcNow();
        var normalized = connection;

        if (connection.AuthorizedAt.AddMonths(6) <= now)
        {
            normalized = connection with
            {
                State = SpotifyConnectionState.ReauthorizationRequired,
                AccessToken = string.Empty,
                RefreshToken = string.Empty,
                RateLimitedUntil = null,
                LastError = "Spotify authorization reached its six-month lifetime."
            };
        }
        else if (connection.State == SpotifyConnectionState.RateLimited &&
                 connection.RateLimitedUntil <= now)
        {
            normalized = connection with
            {
                State = MissingScopes(connection.GrantedScopes).Count == 0
                    ? SpotifyConnectionState.Connected
                    : SpotifyConnectionState.ScopeLimited,
                RateLimitedUntil = null,
                LastError = null
            };
        }
        else if (connection.State is SpotifyConnectionState.Connected or SpotifyConnectionState.ScopeLimited)
        {
            var scopeState = MissingScopes(connection.GrantedScopes).Count == 0
                ? SpotifyConnectionState.Connected
                : SpotifyConnectionState.ScopeLimited;
            if (scopeState != connection.State)
                normalized = connection with { State = scopeState };
        }

        if (!ReferenceEquals(normalized, connection))
            _connections.Save(normalized);

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeScopes(string? scope) =>
        (scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> MissingScopes(IReadOnlyList<string> grantedScopes)
    {
        var granted = grantedScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredScopes.Where(scope => !granted.Contains(scope)).ToArray();
    }

    private static string? BuildWarning(
        SpotifyConnectionRecord connection,
        IReadOnlyList<string> missingScopes,
        int daysUntilReauthorization)
    {
        if (connection.State == SpotifyConnectionState.ReauthorizationRequired)
            return "Spotify must be reauthorized before the agent can use your account.";
        if (connection.State == SpotifyConnectionState.QuotaExceeded)
            return "Spotify's Development Mode quota is exhausted. Automatic retries are stopped.";
        if (connection.State == SpotifyConnectionState.RateLimited)
            return "Spotify is rate limiting requests. Your conversation and plans are preserved.";
        if (connection.State == SpotifyConnectionState.SpotifyUnavailable)
            return "Spotify was unavailable during the last request. You can try again later.";
        if (missingScopes.Count > 0)
            return $"Reauthorize to grant {missingScopes.Count} missing capability scope(s).";
        if (daysUntilReauthorization <= 1)
            return "Spotify reauthorization is due within one day.";
        if (daysUntilReauthorization <= 7)
            return $"Spotify reauthorization is due in {daysUntilReauthorization} days.";
        if (daysUntilReauthorization <= 30)
            return $"Spotify reauthorization is due in {daysUntilReauthorization} days.";
        return null;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_config.ClientId) ||
            string.IsNullOrWhiteSpace(_config.ClientSecret) ||
            !Uri.TryCreate(_config.RedirectUri, UriKind.Absolute, out _))
        {
            throw new SpotifyConnectionException(
                "Spotify ClientId, ClientSecret, and absolute RedirectUri must be configured.",
                "Disconnected",
                HttpStatusCode.ServiceUnavailable);
        }
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
