using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

// ─── API Response Models ─────────────────────────────────────────────────────

public record SpotifyTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn
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
    [property: JsonPropertyName("display_name")] string? DisplayName
);

public record SpotifyPlaylistResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyPlaylistsResponse(
    [property: JsonPropertyName("items")] List<SpotifyPlaylistItem> Items,
    [property: JsonPropertyName("total")] int Total
);

public record SpotifyPlaylistItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("images")] List<SpotifyImage>? Images,
    [property: JsonPropertyName("tracks")] SpotifyPlaylistTracks? Tracks,
    [property: JsonPropertyName("external_urls")] SpotifyExternalUrls? ExternalUrls
);

public record SpotifyPlaylistTracks(
    [property: JsonPropertyName("total")] int Total
);

// ─── Request/Response DTOs for Endpoints ─────────────────────────────────────

public record SpotifySearchRequest(
    string Query,
    int Limit = 20
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

public record SpotifyPlaylistDto(
    string Id,
    string Name,
    string? ImageUrl,
    int TrackCount,
    string? SpotifyUrl
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
