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
/// A proposed Spotify mutation, from draft through execution.
///
/// The plan is the safety model. Nothing writes to Spotify except by executing the
/// <see cref="Steps"/> of a plan that reached <see cref="SpotifyPlanStatus.Executing"/>
/// through <see cref="Services.SpotifyPlanStateMachine"/>, and the targets recorded
/// at build time are the only playlists execution may touch — a confirmed plan can
/// never widen its own scope.
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
    string? Failure = null,
    IReadOnlyList<SpotifyPlanStep>? Steps = null,
    SpotifyPlanPreview? Preview = null,
    IReadOnlyList<SpotifyRestoreManifest>? RestoreManifests = null,
    string? OriginalRequest = null,
    string? SourceDraftId = null,
    /// <summary>Set when this plan is itself the undo of an earlier one.</summary>
    Guid? UndoOfPlanId = null,
    /// <summary>Set once an undo has been executed, so it cannot be run twice.</summary>
    Guid? UndoneByPlanId = null
)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public IReadOnlyList<SpotifyPlanStep> OrderedSteps =>
        (Steps ?? []).OrderBy(step => step.Ordinal).ToList();

    /// <summary>
    /// Undo is offered only for a plan that actually changed something, captured a
    /// way back, and has not already been reverted.
    /// </summary>
    public bool CanUndo =>
        Status is SpotifyPlanStatus.Completed or SpotifyPlanStatus.PartiallyCompleted
        && UndoOfPlanId is null
        && UndoneByPlanId is null
        && RestoreManifests is { Count: > 0 };
}
