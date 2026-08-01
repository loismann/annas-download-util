namespace AnnasArchive.API.Models;

public enum SpotifyConnectionState
{
    Connected,
    ScopeLimited,
    ReauthorizationRequired,
    RateLimited,
    QuotaExceeded,
    SpotifyUnavailable
}

/// <summary>
/// Server-side connection document. The entire serialized record is protected
/// before it is written to SQLite; no token is ever returned by an endpoint.
/// </summary>
public sealed record SpotifyConnectionRecord(
    string OwnerKey,
    string AccountId,
    string SpotifyUserId,
    string? DisplayName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    IReadOnlyList<string> GrantedScopes,
    DateTimeOffset AuthorizedAt,
    SpotifyConnectionState State,
    DateTimeOffset? LastSuccessfulCallAt = null,
    DateTimeOffset? RateLimitedUntil = null,
    string? LastError = null
);

public sealed record SpotifyOAuthCompletion(
    string OwnerKey,
    bool Succeeded,
    string? Error = null
);
