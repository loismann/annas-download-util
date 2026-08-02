using AnnasArchive.API.Services.Spotify;

namespace AnnasArchive.API.Models;

/// <summary>
/// Why a playlist's contents are or are not readable.
///
/// This exists because "unknown is not zero" is a product rule, not a detail: a
/// playlist Spotify will not let us read must never be reported as empty. The
/// prototype collapsed a missing 2026 <c>items</c> field to "0 tracks", which is
/// how a followed playlist full of music looked identical to one you had emptied.
/// </summary>
public enum SpotifyContentsAccess
{
    /// <summary>Spotify returned an item collection. The count is real.</summary>
    Available,

    /// <summary>Metadata is visible but the item collection is absent from the payload.</summary>
    Unavailable,

    /// <summary>The item endpoint answered 403 — followed, but not owned or collaborative.</summary>
    Forbidden,

    /// <summary>
    /// Spotify returned at least one page, but the complete collection could not be
    /// read. Partial contents are useful evidence, but never safe input for cleanup.
    /// </summary>
    Partial
}

/// <summary>
/// What a playlist item actually is. Spotify playlists hold more than catalog
/// tracks, and the awkward cases are the ones that break naive code: podcast
/// episodes have no artists, local files have no playable URI, and an entry whose
/// <c>item</c> is null is a real thing Spotify returns for removed content.
/// </summary>
public enum SpotifyItemKind
{
    Track,
    Episode,
    Local,
    Unavailable
}

public record SpotifyPlaylistItemDto(
    int Position,
    SpotifyItemKind Kind,
    string? Id,
    string? Name,
    string? Uri,
    string Artists,
    string? AlbumName,
    int DurationMs,
    string? SpotifyUrl,
    bool IsLocal,
    DateTimeOffset? AddedAt,
    string? Isrc = null
);

public record SpotifyPlaylistItemsPageDto(
    string PlaylistId,
    IReadOnlyList<SpotifyPlaylistItemDto> Items,
    int Total,
    int Offset,
    int Limit,
    bool HasMore,
    SpotifyContentsAccess Access = SpotifyContentsAccess.Available,
    string? SnapshotId = null
);

public enum SpotifyPlaylistMatchKind
{
    Resolved,
    Ambiguous,
    NotFound
}

/// <summary>
/// Outcome of turning a phrase like "my Road Trip playlist" into one playlist.
/// Ambiguity is a first-class result, not an error and not a guess — playlist
/// names are not unique in Spotify, so the user picks.
/// </summary>
public record SpotifyPlaylistResolution(
    SpotifyPlaylistMatchKind Kind,
    SpotifyPlaylistDto? Playlist,
    IReadOnlyList<SpotifyPlaylistDto> Candidates,
    string? MatchedBy = null
)
{
    public static SpotifyPlaylistResolution Resolved(SpotifyPlaylistDto playlist, string matchedBy) =>
        new(SpotifyPlaylistMatchKind.Resolved, playlist, [playlist], matchedBy);

    public static SpotifyPlaylistResolution Ambiguous(IReadOnlyList<SpotifyPlaylistDto> candidates) =>
        new(SpotifyPlaylistMatchKind.Ambiguous, null, candidates);

    public static SpotifyPlaylistResolution NotFound() =>
        new(SpotifyPlaylistMatchKind.NotFound, null, []);
}

/// <summary>
/// A best-effort count of how often a playlist appeared as the playback context in
/// the recent history Spotify exposes. Deliberately not called "most listened to":
/// Spotify returns a bounded window, omits private sessions, and reports no context
/// at all for many plays.
/// </summary>
public record SpotifyRecentPlaylistContextDto(
    string PlaylistId,
    string? Name,
    int ObservedPlays,
    string? SpotifyUrl
);

public record SpotifyRecentTrackDto(
    SpotifyPlaylistItemDto Track,
    DateTimeOffset? PlayedAt,
    string? ContextType,
    string? ContextUri
);

// ─── Typed command envelope ──────────────────────────────────────────────────

/// <summary>
/// What the language model is allowed to return. The model classifies intent and
/// may echo back a name the user typed; it never supplies Spotify IDs, ownership,
/// scopes, counts, or URIs. Those are resolved server-side from Spotify's own
/// responses, so a confident wrong guess cannot become a fact.
/// </summary>
public record SpotifyCommandEnvelope(
    int SchemaVersion,
    string Action,
    SpotifyCommandArguments? Arguments = null,
    double Confidence = 0d,
    string? Clarification = null
);

public record SpotifyCommandArguments(
    string? Query = null,
    string? PlaylistReference = null,
    int? Limit = null,
    string? TimeRange = null
);

/// <summary>
/// A parsed, validated command. Reaching this type means the action is one the
/// server owns and its required arguments are present.
/// </summary>
public record SpotifyValidatedCommand(
    SpotifyReadAction Action,
    SpotifyCommandArguments Arguments,
    double Confidence,
    string? Clarification = null
);

public record SpotifyConversationRequest(
    string Message,
    string? PlaylistId = null,
    int? Offset = null,
    string? DraftId = null
);

public record SpotifyConversationResponse(
    string Action,
    double Confidence,
    string Message,
    object? Data = null,
    string? Clarification = null
);
