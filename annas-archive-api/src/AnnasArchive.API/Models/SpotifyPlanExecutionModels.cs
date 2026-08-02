using System.Text.Json.Serialization;

namespace AnnasArchive.API.Models;

/// <summary>
/// One Spotify write. Plans are ordered lists of these, and execution stops at the
/// first failure rather than pressing on — a merge whose target was never populated
/// must not go on to remove the sources.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyPlanStepKind
{
    CreatePlaylist,
    AddItems,
    RemoveItems,
    ReplaceItems,
    ReorderItems,
    ChangeDetails,
    RemoveFromLibrary,

    /// <summary>
    /// Re-reads a playlist and fails unless it holds at least
    /// <see cref="SpotifyPlanStep.ExpectedItemCount"/> items. It writes nothing; its
    /// whole purpose is to sit between a merge's population and its source removal
    /// so that "the target is full" is something we checked rather than assumed.
    /// </summary>
    VerifyPlaylistPopulated,

    /// <summary>Re-follows a playlist a previous plan removed. Undo only.</summary>
    AddToLibrary
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyPlanStepStatus
{
    Pending,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>
/// A single step's payload. Which fields matter depends on <see cref="Kind"/>;
/// the rest stay null rather than being overloaded, so a persisted plan reads
/// honestly months later.
///
/// <paramref name="ResultingSnapshotId"/> is what Spotify returned after the write.
/// It is the receipt: it proves the step landed, and it is what a later undo has to
/// check before assuming the playlist still looks the way this step left it.
/// </summary>
public sealed record SpotifyPlanStep(
    int Ordinal,
    SpotifyPlanStepKind Kind,
    string? PlaylistId,
    string? PlaylistName,
    IReadOnlyList<string>? Uris = null,
    IReadOnlyList<int>? Positions = null,
    string? Name = null,
    string? Description = null,
    bool? IsPublic = null,
    int? RangeStart = null,
    int? InsertBefore = null,
    int? RangeLength = null,
    /// <summary>What a <see cref="SpotifyPlanStepKind.VerifyPlaylistPopulated"/> step must find.</summary>
    int? ExpectedItemCount = null,
    SpotifyPlanStepStatus Status = SpotifyPlanStepStatus.Pending,
    string? ResultingSnapshotId = null,
    string? CreatedPlaylistId = null,
    string? Failure = null
);

/// <summary>
/// Everything needed to put a playlist back the way it was, captured *before* the
/// write. Undo is best-effort and this records why: a removed local file cannot be
/// re-added through the API at all, and exact positions can only be restored while
/// the snapshot still matches.
/// </summary>
public sealed record SpotifyRestoreManifest(
    string PlaylistId,
    string PlaylistName,
    string? SnapshotId,
    IReadOnlyList<string> OrderedUris,
    string? PreviousName = null,
    string? PreviousDescription = null,
    bool? PreviousIsPublic = null,
    IReadOnlyList<string>? UnrestorableItems = null,
    /// <summary>
    /// Set when the step removed this playlist from the library. Undo re-follows the
    /// URI. Spotify never deleted anything, so the playlist itself still exists — but
    /// re-following only works while it does, which is why undo re-checks first.
    /// </summary>
    string? RemovedLibraryUri = null,
    /// <summary>
    /// Set when the step created this playlist. Undoing a creation means removing it
    /// from the library again — Spotify has no delete — so this is the one manifest
    /// whose inverse is itself a removal.
    /// </summary>
    bool WasCreated = false
);

/// <summary>
/// What the user sees before confirming. Deliberately separate from the stored plan:
/// the review surface must describe consequences in plain language, and the numbers
/// here are computed once at build time so the screen cannot disagree with the plan.
/// </summary>
public sealed record SpotifyPlanPreview(
    string Summary,
    string ConfirmLabel,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> Warnings,
    bool RequiresHighImpactAcknowledgement,
    int ItemsAdded = 0,
    int ItemsRemoved = 0,
    int ItemsSkippedAsDuplicates = 0,
    int ItemsUnresolved = 0,
    int PlaylistsAffected = 0
);

/// <summary>
/// What is left to do after a plan stopped part-way, and what may safely be done
/// about it.
///
/// The distinction that matters: a *skipped* step was never attempted, so re-running
/// it is unambiguous. A *failed* step may have landed partially, which is why resume
/// re-reads the playlist before acting rather than replaying the original payload.
/// </summary>
public sealed record SpotifyPlanRecovery(
    bool CanResume,
    int StepsSucceeded,
    int StepsFailed,
    int StepsNotAttempted,
    string Advice
);

public sealed record SpotifyPlanDto(
    Guid Id,
    SpotifyPlanAction Action,
    SpotifyPlanSafetyTier SafetyTier,
    SpotifyPlanStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SpotifyPlanTarget> Targets,
    SpotifyPlanPreview Preview,
    IReadOnlyList<SpotifyPlanStep> Steps,
    string? OriginalRequest,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAtUtc,
    string? Failure,
    bool CanUndo,
    Guid? UndoOfPlanId,
    SpotifyPlanRecovery? Recovery = null
);

// ─── audit ───────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotifyAuditEventKind
{
    PlanBuilt,
    PlanConfirmed,
    PlanCancelled,
    PlanExpired,
    PlanResumed,
    StepSucceeded,
    StepFailed,
    PlanCompleted,
    PlanPartiallyCompleted,
    PlanFailed,
    PlanReverted
}

/// <summary>
/// Append-only. Records who asked, who confirmed, and exactly what changed — with
/// sanitized names and IDs only. Never tokens, never whole Spotify payloads.
/// </summary>
public sealed record SpotifyAuditEvent(
    Guid Id,
    Guid PlanId,
    SpotifyAuditEventKind Kind,
    DateTimeOffset AtUtc,
    string? ApplicationUser,
    string? SpotifyAccountId,
    string Detail
);

// ─── requests ────────────────────────────────────────────────────────────────

public sealed record SpotifyBuildPlanRequest(
    SpotifyPlanAction Action,
    string? PlaylistReference = null,
    string? PlaylistId = null,
    string? DraftId = null,
    string? Name = null,
    string? Description = null,
    bool? IsPublic = null,
    IReadOnlyList<string>? Uris = null,
    IReadOnlyList<int>? Positions = null,
    IReadOnlyList<string>? OrderedUris = null,
    int? RangeStart = null,
    int? InsertBefore = null,
    int? RangeLength = null,
    string? OriginalRequest = null,

    // ─── phase 8: multi-playlist work ───────────────────────────────────────
    /// <summary>Names the user gave, for merge and library removal.</summary>
    IReadOnlyList<string>? PlaylistReferences = null,
    /// <summary>Already-resolved IDs, when the user picked from disambiguation cards.</summary>
    IReadOnlyList<string>? PlaylistIds = null,
    /// <summary>An existing merge destination. Absent means create a new private one.</summary>
    string? TargetPlaylistReference = null,
    string? TargetPlaylistId = null,
    /// <summary>
    /// Whether a merge also removes its sources from the library. Off by default:
    /// the spec's merge policy leaves sources alone unless removal is asked for.
    /// </summary>
    bool RemoveSources = false
);

public sealed record SpotifyConfirmPlanRequest(bool HighImpactAcknowledged = false);
