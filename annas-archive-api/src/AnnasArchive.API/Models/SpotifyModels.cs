using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

// ─── API Response Models ─────────────────────────────────────────────────────

public record SpotifyTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("scope")] string? Scope = null
);

public record SpotifySearchResponse(
    [property: JsonPropertyName("tracks")] SpotifyTracksContainer? Tracks
);

public record SpotifyTracksContainer(
    [property: JsonPropertyName("items")] List<SpotifyTrackItem> Items,
    [property: JsonPropertyName("total")] int Total
);

public record SpotifyTrackItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("artists")] List<SpotifyArtist> Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum Album,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyArtist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

public record SpotifyAlbum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] List<SpotifyImage> Images
);

public record SpotifyImage(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("width")] int? Width
);

public record SpotifyExternalUrls(
    [property: JsonPropertyName("spotify")] string? Spotify
);

public record SpotifyUserProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("account_id")] string? AccountId = null
);

public record SpotifyTokenErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? Description = null
);

public record SpotifyPlaylistResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyPlaylistsResponse(
    [property: JsonPropertyName("items")] List<SpotifyPlaylistItem> Items,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("next")] string? Next = null,
    [property: JsonPropertyName("offset")] int Offset = 0,
    [property: JsonPropertyName("limit")] int Limit = 20
);

public record SpotifyPlaylistItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] List<SpotifyImage>? Images,
    [property: JsonPropertyName("items")] SpotifyPlaylistItemsSummary? ItemSummary,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls,
    [property: JsonPropertyName("owner")] SpotifyPlaylistOwner? Owner = null,
    [property: JsonPropertyName("public")] bool? Public = null,
    [property: JsonPropertyName("collaborative")] bool Collaborative = false,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId = null,
    [property: JsonPropertyName("uri")] string? Uri = null
);

public record SpotifyPlaylistItemsSummary(
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("total")] int Total
);

public record SpotifyPlaylistOwner(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyPlaylistItemsResponse(
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("previous")] string? Previous,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("items")] List<SpotifyPlaylistEntry> Items
);

public record SpotifyPlaylistEntry(
    [property: JsonPropertyName("added_at")] DateTimeOffset? AddedAt,
    [property: JsonPropertyName("added_by")] SpotifyPlaylistOwner? AddedBy,
    [property: JsonPropertyName("is_local")] bool IsLocal,
    [property: JsonPropertyName("item")] SpotifyPlaylistPlayableItem? Item
);

/// <summary>
/// The 2026 playlist item wrapper can contain a track or an episode. Properties
/// that only exist on one kind deliberately remain nullable; callers must branch
/// on <see cref="Type"/> instead of assuming every item is a catalog track.
/// </summary>
public record SpotifyPlaylistPlayableItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("artists")] List<SpotifyArtist>? Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum? Album,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls,
    [property: JsonPropertyName("is_local")] bool IsLocal = false
);

public record SpotifyRecentlyPlayedResponse(
    [property: JsonPropertyName("items")] List<SpotifyRecentlyPlayedEntry>? Items
);

public record SpotifyRecentlyPlayedEntry(
    [property: JsonPropertyName("played_at")] DateTimeOffset? PlayedAt,
    [property: JsonPropertyName("context")] SpotifyPlaybackContext? Context
);

/// <summary>
/// The playback context a track was played from. Frequently null — Spotify reports
/// no context for many plays — and often an album or artist rather than a playlist.
/// Both cases mean "no evidence", never "not listened to".
/// </summary>
public record SpotifyPlaybackContext(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyTopItemsResponse(
    [property: JsonPropertyName("items")] List<SpotifyTopItem>? Items
);

/// <summary>
/// /me/top/tracks and /me/top/artists return the same envelope with different item
/// shapes — a track carries artists and an album, an artist carries genres and
/// neither. Both are read through this one nullable-heavy record.
/// </summary>
public record SpotifyTopItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("artists")] List<SpotifyArtist>? Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum? Album,
    [property: JsonPropertyName("genres")] List<string>? Genres,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

// ─── Request/Response DTOs for Endpoints ─────────────────────────────────────

public record SpotifySearchRequest(
    string Query,
    int Limit = 10
);

public record SpotifyTrackDto(
    string Id,
    string Name,
    string Uri,
    int DurationMs,
    string Artists,
    string AlbumName,
    string? AlbumArtUrl,
    string? SpotifyUrl
);

public record SpotifySearchResultDto(
    List<SpotifyTrackDto> Tracks,
    int Total
);

public record CreatePlaylistRequest(
    string Name,
    string? Description = null,
    bool Public = false
);

public record AddTracksRequest(
    string PlaylistId,
    List<string> TrackUris
);

/// <summary>
/// <paramref name="TrackCount"/> is nullable on purpose. Spotify omits the
/// <c>items</c> summary for playlists whose contents it will not expose, and the
/// only honest rendering of that is "unavailable" — never 0. Pair it with
/// <paramref name="ContentsAvailable"/>: false means the count is unknown, not zero.
/// </summary>
public record SpotifyPlaylistDto(
    string Id,
    string Name,
    string? ImageUrl,
    int? TrackCount,
    string? SpotifyUrl,
    bool ContentsAvailable = true,
    string? SnapshotId = null,
    string? OwnerId = null,
    string? OwnerName = null,
    bool IsOwnedByUser = false,
    bool IsCollaborative = false,
    bool? IsPublic = null,
    string? Uri = null
);

public record SpotifyAuthorizeRequest(bool ForceDialog = false);

public record SpotifyAuthorizeResponse(string AuthorizationUrl);

public record SpotifyConnectionStatusDto(
    string State,
    bool IsConnected,
    string? AccountId,
    string? SpotifyUserId,
    string? DisplayName,
    IReadOnlyList<string> GrantedScopes,
    IReadOnlyList<string> MissingScopes,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? ReauthorizationDueAt,
    int? DaysUntilReauthorization,
    DateTimeOffset? LastSuccessfulCallAt,
    DateTimeOffset? RateLimitedUntil,
    string? Warning,
    string? LastError
);

public record SpotifyConnectionErrorDto(
    string Error,
    string State,
    string? Reason = null,
    int? RetryAfterSeconds = null
);

// ─── AI Command Models ───────────────────────────────────────────────────────

public record SpotifyCommandRequest(
    string Message,
    string? Context = null
);

public record SpotifyCommandResponse(
    ParsedSpotifyCommand Parsed,
    string NaturalResponse,
    object? Data = null,
    string? Error = null
);

public record ParsedSpotifyCommand(
    string Action,
    string? SearchQuery = null,
    string? PlaylistName = null,
    string? PlaylistId = null,
    List<string>? TrackUris = null,
    string? Description = null,
    double Confidence = 1.0,
    string? ClarificationNeeded = null,
    // Vibe-based generation fields
    string? VibeDescription = null,
    int? TrackCount = null,
    List<string>? ClarifyingQuestions = null,
    bool ReadyToGenerate = false
);

// ─── Vibe Generation Models ──────────────────────────────────────────────────

public record GeneratedSongSuggestion(
    string Artist,
    string Title,
    string? Reason = null
);

public record VibeGenerationResult(
    List<SpotifyTrackDto> FoundTracks,
    List<string> NotFoundSongs,
    SpotifyPlaylistDto? CreatedPlaylist = null
);
