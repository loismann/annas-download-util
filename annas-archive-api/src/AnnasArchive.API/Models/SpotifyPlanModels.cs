using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyPlanStatus
{
    Draft,
    AwaitingConfirmation,
    Executing,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled,
    Expired,
    Reverted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyPlanSafetyTier
{
    ReadOnly = 0,
    Additive = 1,
    Modifying = 2,
    HighImpact = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyPlanAction
{
    CreatePlaylist,
    RenamePlaylist,
    ChangePlaylistDetails,
    AddItems,
    RemoveItems,
    ReorderItems,
    ReplaceItems,
    MergePlaylists,
    RemovePlaylistsFromLibrary,
    RestorePreviousChange
}

public sealed record SpotifyPlanTarget(
    string PlaylistId,
    string DisplayName,
    string? SnapshotId = null
);

/// <summary>
/// Immutable phase-one representation of a proposed Spotify mutation. Persistence,
/// step manifests, and execution are intentionally added in later phases; this
/// record establishes the lifecycle and safety contract they must follow.
/// </summary>
public sealed record SpotifyChangePlan(
    Guid Id,
    SpotifyPlanAction Action,
    SpotifyPlanSafetyTier SafetyTier,
    SpotifyPlanStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SpotifyPlanTarget> Targets,
    string? ConfirmedBy = null,
    DateTimeOffset? ConfirmedAtUtc = null,
    string? Failure = null
)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;
}
