namespace AnnasArchive.API.Models;

/// <summary>
/// One playlist's contents, fully paged, plus how much of it we were allowed to see.
/// </summary>
public record SpotifyPlaylistContents(
    SpotifyPlaylistDto Playlist,
    IReadOnlyList<SpotifyPlaylistItemDto> Items,
    SpotifyContentsAccess Access,
    string? SnapshotId
)
{
    public bool IsReadable => Access == SpotifyContentsAccess.Available;
}

public enum SpotifyDuplicateConfidence
{
    /// <summary>The same Spotify URI. Not a judgement call.</summary>
    Exact,

    /// <summary>Same normalized artist and title. Probably the same recording, possibly not.</summary>
    Probable,

    /// <summary>Different Spotify URIs with the same recording-level ISRC.</summary>
    Recording
}

public record SpotifyDuplicateItemGroup(
    string PlaylistId,
    string PlaylistName,
    string Label,
    SpotifyDuplicateConfidence Confidence,
    IReadOnlyList<int> Positions
);

public record SpotifyEmptyPlaylist(string PlaylistId, string Name);

/// <summary>
/// Two playlists that overlap. <paramref name="Overlap"/> is Jaccard — shared over
/// combined — so it is symmetric and does not flatter a small playlist contained in
/// a large one. That case is reported by <paramref name="SupersetOf"/> instead.
/// </summary>
public record SpotifyPlaylistOverlap(
    string LeftId,
    string LeftName,
    string RightId,
    string RightName,
    int SharedItems,
    int LeftOnlyItems,
    int RightOnlyItems,
    double Overlap,
    bool Identical,
    string? SupersetOf = null
);

public record SpotifyNamingCollision(string Normalized, IReadOnlyList<SpotifyPlaylistDto> Playlists);

/// <summary>
/// The result of a library scan. Everything is evidence for a human decision — no
/// part of this authorises a change, and the unreadable list is what stops a
/// cleanup plan being built on a partial view.
/// </summary>
public record SpotifyLibraryAnalysis(
    int PlaylistsScanned,
    int PlaylistsRead,
    IReadOnlyList<SpotifyPlaylistDto> Unreadable,
    IReadOnlyList<SpotifyEmptyPlaylist> Empty,
    IReadOnlyList<SpotifyDuplicateItemGroup> DuplicateItems,
    IReadOnlyList<SpotifyPlaylistOverlap> OverlappingPlaylists,
    IReadOnlyList<SpotifyNamingCollision> NamingCollisions,
    IReadOnlyList<SpotifyPlaylistDto>? RecentlyObserved = null,
    int UsageUnknown = 0,
    IReadOnlyList<string>? Limitations = null,
    DateTimeOffset? GeneratedAt = null
);

// ─── top items and the known-music index ─────────────────────────────────────

public record SpotifyTopItemDto(string Id, string Name, string? Detail, string? SpotifyUrl, int Rank);

public record SpotifyTopItemsDto(
    string Kind,
    string TimeRange,
    IReadOnlyList<SpotifyTopItemDto> Items
);

/// <summary>
/// Everything we can honestly claim Paul has been exposed to, and — just as
/// importantly — how partial that claim is. <paramref name="UnreadablePlaylists"/>
/// is the reason nothing here may ever be phrased as "you have never heard this".
/// </summary>
public record SpotifyKnownMusicIndex(
    IReadOnlySet<string> ArtistKeys,
    IReadOnlySet<string> TrackKeys,
    int PlaylistsIncluded,
    int UnreadablePlaylists,
    bool IncludesTopItems,
    bool IncludesRecentHistory,
    int ExplicitOverrides = 0
);

public record SpotifyKnownMusicReport(
    SpotifyKnownMusicIndex Index,
    string Coverage,
    DateTimeOffset GeneratedAt
);

public enum SpotifyInventoryJobState
{
    NotStarted,
    Queued,
    Running,
    Complete,
    Partial,
    Failed
}

public record SpotifyInventoryStatusDto(
    string? JobId,
    SpotifyInventoryJobState State,
    int TotalPlaylists,
    int ProcessedPlaylists,
    int ReadablePlaylists,
    int PartialPlaylists,
    int UnreadablePlaylists,
    DateTimeOffset? StartedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastInventoryAt,
    string? Message = null
);

public record SpotifyKnownMusicOverrideRequest(
    string Kind,
    string Name,
    bool Known,
    string? Artist = null
);

public record SpotifyKnownMusicOverrideResult(
    string Kind,
    string Name,
    bool Known,
    DateTimeOffset UpdatedAt
);
