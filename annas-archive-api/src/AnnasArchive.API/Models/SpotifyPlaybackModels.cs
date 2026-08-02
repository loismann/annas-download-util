using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

// ─── Spotify wire shapes ─────────────────────────────────────────────────────

public record SpotifyDeviceResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("is_restricted")] bool IsRestricted,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("volume_percent")] int? VolumePercent
);

public record SpotifyDevicesResponse(
    [property: JsonPropertyName("devices")] List<SpotifyDeviceResponse> Devices
);

public record SpotifyPlaybackStateResponse(
    [property: JsonPropertyName("device")] SpotifyDeviceResponse? Device,
    [property: JsonPropertyName("is_playing")] bool IsPlaying,
    [property: JsonPropertyName("progress_ms")] int? ProgressMs,
    [property: JsonPropertyName("item")] SpotifyTrackItem? Item
);

// ─── What the browser sees ───────────────────────────────────────────────────

/// <summary>
/// One thing that can play audio. <paramref name="IsRestricted"/> matters: Spotify
/// returns devices it will not let the Web API control, and offering those as a
/// destination produces a play button that silently does nothing.
/// </summary>
public record SpotifyDeviceDto(
    string Id,
    string Name,
    string Type,
    bool IsActive,
    bool IsRestricted,
    int? VolumePercent
);

/// <summary>
/// What is playing right now, on whatever device. Null <paramref name="Device"/>
/// means Spotify reports nothing active anywhere — which is the normal state, not
/// an error, and the UI has to offer to start something rather than look broken.
/// </summary>
public record SpotifyPlaybackStateDto(
    SpotifyDeviceDto? Device,
    bool IsPlaying,
    int ProgressMs,
    SpotifyTrackDto? Track
);

public record SpotifyPlayRequest(
    string? DeviceId = null,
    /// <summary>Explicit tracks to play. Takes precedence over <see cref="ContextUri"/>.</summary>
    IReadOnlyList<string>? Uris = null,
    /// <summary>A playlist to play, so "next track" follows the playlist rather than stopping.</summary>
    string? ContextUri = null,
    int? OffsetPosition = null,
    int PositionMs = 0
);

public record SpotifyTransferRequest(string DeviceId, bool Play = true);

/// <summary>
/// A short-lived Spotify access token for the Web Playback SDK.
///
/// The SDK runs in the page and demands a token client-side — there is no
/// server-side variant. So this endpoint necessarily hands the browser a token
/// carrying every granted scope. That is acceptable only because the surface is
/// admin-authenticated and same-origin; it is the one place Spotify credentials
/// leave the server, and it must never be widened to an anonymous route.
/// </summary>
public record SpotifyPlaybackTokenDto(string AccessToken, int ExpiresInSeconds);
