using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

/// <summary>
/// Turns a request into a reviewable plan, or refuses.
///
/// Nothing here talks to Spotify — it works on contents already fetched — so the
/// preview shown to the user is computed from the same data the steps are built
/// from and the two cannot disagree. Refusal is a first-class outcome: a plan that
/// would exceed the bulk ceiling, or touch a playlist Spotify will not let us read,
/// is better returned as an explanation than as steps that fail halfway.
/// </summary>
public static class SpotifyPlanBuilder
{
    /// <summary>
    /// Spec defaults. Larger work is split into separately confirmed plans rather
    /// than executed as one very long transaction that can half-fail.
    /// </summary>
    public const int MaxPlaylistsPerPlan = 20;
    public const int MaxItemMutationsPerPlan = 500;

    public sealed record Result(SpotifyChangePlan? Plan, string? Refusal)
    {
        public bool Refused => Plan is null;
        public static Result Refuse(string why) => new(null, why);
    }

    // ─── phase 6: additive ───────────────────────────────────────────────────

    /// <summary>
    /// Create a playlist and fill it. Unresolved draft candidates are counted and
    /// surfaced, never silently substituted with a near-match — a wrong song added
    /// quietly is worse than a gap the user can see.
    /// </summary>
    public static Result CreateFromDraft(
        SpotifyDiscoveryDraft draft, string? nameOverride, bool isPublic, DateTimeOffset nowUtc,
        string? originalRequest = null)
    {
        var resolved = draft.Candidates
            .Where(c => c.Resolution == SpotifyCandidateResolution.Resolved && c.Track is not null)
            .OrderBy(c => c.Position)
            .ToList();

        var unresolved = draft.Candidates.Count - resolved.Count;
        var name = string.IsNullOrWhiteSpace(nameOverride) ? draft.Name : nameOverride.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return Result.Refuse("The playlist needs a name before I can create it.");

        if (resolved.Count == 0)
        {
            return Result.Refuse(
                "None of the candidates in this draft resolved to a Spotify track, so there is "
                + "nothing to add. Refine the draft first.");
        }

        if (resolved.Count > MaxItemMutationsPerPlan)
            return Result.Refuse(TooManyItems(resolved.Count));

        var uris = resolved.Select(c => c.Track!.Uri).ToList();

        var steps = new List<SpotifyPlanStep>
        {
            new(0, SpotifyPlanStepKind.CreatePlaylist, null, name, Name: name, IsPublic: isPublic,
                Description: draft.Summary),
            // PlaylistId is null until step 0 runs; the executor fills it from the
            // created playlist. A plan cannot name an ID that does not exist yet.
            new(1, SpotifyPlanStepKind.AddItems, null, name, Uris: uris)
        };

        var effects = new List<string>
        {
            $"Create a new {(isPublic ? "public" : "private")} playlist called “{name}”",
            $"Add {resolved.Count} {Plural(resolved.Count, "track", "tracks")} to it"
        };

        var warnings = new List<string>();
        if (unresolved > 0)
        {
            warnings.Add($"{unresolved} candidate(s) never resolved to a Spotify track and will be "
                       + "left out rather than replaced with a guess.");
        }

        return Ready(
            SpotifyPlanAction.CreatePlaylist,
            [],
            steps,
            new SpotifyPlanPreview(
                $"Create “{name}” with {resolved.Count} {Plural(resolved.Count, "track", "tracks")}",
                $"Create “{name}”",
                effects, warnings,
                RequiresHighImpactAcknowledgement: false,
                ItemsAdded: resolved.Count,
                ItemsUnresolved: unresolved,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest,
            sourceDraftId: draft.Id);
    }

    /// <summary>
    /// Add items to an existing playlist. URIs already present are reported as
    /// skipped rather than added again — Spotify permits duplicates, so silently
    /// creating them would be the tool making a mess the user then has to find.
    /// </summary>
    public static Result AddItems(
        SpotifyPlaylistContents target, IReadOnlyList<string> uris, DateTimeOffset nowUtc,
        string? originalRequest = null)
    {
        if (Unwritable(target) is { } refusal)
            return Result.Refuse(refusal);

        var existing = target.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
            .Select(i => i.Uri!)
            .ToHashSet(StringComparer.Ordinal);

        var wanted = uris.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        var toAdd = wanted.Where(u => !existing.Contains(u)).ToList();
        var skipped = wanted.Count - toAdd.Count;

        if (toAdd.Count == 0)
        {
            return Result.Refuse(
                $"Every one of those {wanted.Count} {Plural(wanted.Count, "track is", "tracks are")} "
                + $"already in “{target.Playlist.Name}”, so there is nothing to add.");
        }

        if (toAdd.Count > MaxItemMutationsPerPlan)
            return Result.Refuse(TooManyItems(toAdd.Count));

        var warnings = new List<string>();
        if (skipped > 0)
            warnings.Add($"{skipped} already in the playlist and will be skipped, not duplicated.");

        return Ready(
            SpotifyPlanAction.AddItems,
            [TargetOf(target)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.AddItems, target.Playlist.Id, target.Playlist.Name, Uris: toAdd)],
            new SpotifyPlanPreview(
                $"Add {toAdd.Count} {Plural(toAdd.Count, "track", "tracks")} to “{target.Playlist.Name}”",
                $"Add {toAdd.Count} {Plural(toAdd.Count, "track", "tracks")}",
                [$"Add {toAdd.Count} {Plural(toAdd.Count, "track", "tracks")} to “{target.Playlist.Name}”"],
                warnings,
                RequiresHighImpactAcknowledgement: false,
                ItemsAdded: toAdd.Count,
                ItemsSkippedAsDuplicates: skipped,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    // ─── phase 7: edits ──────────────────────────────────────────────────────

    public static Result Rename(
        SpotifyPlaylistDto playlist, string newName, DateTimeOffset nowUtc, string? originalRequest = null)
    {
        if (!playlist.IsOwnedByUser && !playlist.IsCollaborative)
            return Result.Refuse(NotYours(playlist.Name));

        var trimmed = newName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return Result.Refuse("What should I rename it to?");

        if (string.Equals(trimmed, playlist.Name, StringComparison.Ordinal))
            return Result.Refuse($"“{playlist.Name}” is already called that.");

        return Ready(
            SpotifyPlanAction.RenamePlaylist,
            [TargetOf(playlist)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.ChangeDetails, playlist.Id, playlist.Name, Name: trimmed)],
            new SpotifyPlanPreview(
                $"Rename “{playlist.Name}” to “{trimmed}”",
                "Rename it",
                [$"“{playlist.Name}” becomes “{trimmed}”"],
                [],
                RequiresHighImpactAcknowledgement: false,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    public static Result ChangeDetails(
        SpotifyPlaylistDto playlist, string? name, string? description, bool? isPublic,
        DateTimeOffset nowUtc, string? originalRequest = null)
    {
        if (!playlist.IsOwnedByUser && !playlist.IsCollaborative)
            return Result.Refuse(NotYours(playlist.Name));

        var effects = new List<string>();
        if (!string.IsNullOrWhiteSpace(name) && name != playlist.Name)
            effects.Add($"Name: “{playlist.Name}” → “{name}”");
        if (description is not null)
            effects.Add($"Description set to “{Truncate(description)}”");
        if (isPublic is not null && isPublic != playlist.IsPublic)
            effects.Add($"Visibility: {(isPublic.Value ? "public" : "private")}");

        if (effects.Count == 0)
            return Result.Refuse($"Nothing about “{playlist.Name}” would change.");

        var warnings = isPublic == true
            ? new List<string> { "Making a playlist public means anyone with the link can see it." }
            : [];

        return Ready(
            SpotifyPlanAction.ChangePlaylistDetails,
            [TargetOf(playlist)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.ChangeDetails, playlist.Id, playlist.Name,
                Name: name, Description: description, IsPublic: isPublic)],
            new SpotifyPlanPreview(
                $"Update {effects.Count} thing(s) about “{playlist.Name}”",
                "Apply the changes",
                effects, warnings,
                RequiresHighImpactAcknowledgement: false,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    /// <summary>
    /// Remove specific items. Positions are shown because "remove Mystery Train"
    /// is ambiguous when it appears three times — the preview says exactly which
    /// occurrences go.
    /// </summary>
    public static Result RemoveItems(
        SpotifyPlaylistContents target, IReadOnlyList<string> uris, DateTimeOffset nowUtc,
        string? originalRequest = null)
    {
        if (Unwritable(target) is { } refusal)
            return Result.Refuse(refusal);

        var wanted = uris.Where(u => !string.IsNullOrWhiteSpace(u)).ToHashSet(StringComparer.Ordinal);
        var matches = target.Items.Where(i => i.Uri is not null && wanted.Contains(i.Uri)).ToList();

        if (matches.Count == 0)
            return Result.Refuse($"I could not find those items in “{target.Playlist.Name}”.");

        if (matches.Count > MaxItemMutationsPerPlan)
            return Result.Refuse(TooManyItems(matches.Count));

        var names = matches.Take(5).Select(i => i.Name ?? i.Uri!).ToList();
        var effects = new List<string>
        {
            $"Remove {matches.Count} {Plural(matches.Count, "item", "items")} from “{target.Playlist.Name}”: "
            + string.Join(", ", names) + (matches.Count > names.Count ? ", …" : "")
        };

        var warnings = new List<string>();
        var locals = matches.Count(i => i.Kind == SpotifyItemKind.Local);
        if (locals > 0)
        {
            warnings.Add($"{locals} local {Plural(locals, "file", "files")} — the API cannot add those "
                       + "back, so removing them cannot be undone here.");
        }

        return Ready(
            SpotifyPlanAction.RemoveItems,
            [TargetOf(target)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.RemoveItems, target.Playlist.Id, target.Playlist.Name,
                Uris: matches.Select(i => i.Uri!).Distinct(StringComparer.Ordinal).ToList(),
                Positions: matches.Select(i => i.Position).OrderBy(p => p).ToList())],
            new SpotifyPlanPreview(
                $"Remove {matches.Count} {Plural(matches.Count, "item", "items")} from “{target.Playlist.Name}”",
                $"Remove {matches.Count} {Plural(matches.Count, "item", "items")}",
                effects, warnings,
                RequiresHighImpactAcknowledgement: false,
                ItemsRemoved: matches.Count,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    /// <summary>
    /// Replace the whole item list. High impact: everything not in the new list is
    /// gone, so this needs a second acknowledgement and a complete restore manifest.
    /// </summary>
    public static Result ReplaceItems(
        SpotifyPlaylistContents target, IReadOnlyList<string> orderedUris, DateTimeOffset nowUtc,
        string? originalRequest = null)
    {
        if (Unwritable(target) is { } refusal)
            return Result.Refuse(refusal);

        var clean = orderedUris.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (clean.Count > MaxItemMutationsPerPlan)
            return Result.Refuse(TooManyItems(clean.Count));

        var existing = target.Items.Where(i => i.Uri is not null).Select(i => i.Uri!).ToList();
        var dropped = existing.Where(u => !clean.Contains(u, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Count();

        var warnings = new List<string>
        {
            "Replacing discards the current contents entirely. I will record the existing "
            + "order first so this can be undone while the playlist stays untouched."
        };

        var unrestorable = target.Items.Count(i => i.Kind == SpotifyItemKind.Local);
        if (unrestorable > 0)
        {
            warnings.Add($"{unrestorable} local {Plural(unrestorable, "file", "files")} cannot be restored "
                       + "by the API, so an undo would not bring those back.");
        }

        return Ready(
            SpotifyPlanAction.ReplaceItems,
            [TargetOf(target)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.ReplaceItems, target.Playlist.Id, target.Playlist.Name,
                Uris: clean)],
            new SpotifyPlanPreview(
                $"Replace the contents of “{target.Playlist.Name}” with {clean.Count} "
                + $"{Plural(clean.Count, "track", "tracks")}",
                "Replace the contents",
                [$"“{target.Playlist.Name}” goes from {existing.Count} to {clean.Count} items",
                 $"{dropped} currently in it would no longer be"],
                warnings,
                RequiresHighImpactAcknowledgement: true,
                ItemsAdded: clean.Count,
                ItemsRemoved: dropped,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    public static Result ReorderItems(
        SpotifyPlaylistContents target, int rangeStart, int insertBefore, int rangeLength,
        DateTimeOffset nowUtc, string? originalRequest = null)
    {
        if (Unwritable(target) is { } refusal)
            return Result.Refuse(refusal);

        var count = target.Items.Count;
        var length = Math.Max(1, rangeLength);

        if (rangeStart < 0 || rangeStart >= count)
            return Result.Refuse($"“{target.Playlist.Name}” has {count} items, so position {rangeStart + 1} does not exist.");

        if (insertBefore < 0 || insertBefore > count)
            return Result.Refuse($"There is no position {insertBefore + 1} in “{target.Playlist.Name}” to move to.");

        if (rangeStart == insertBefore)
            return Result.Refuse("That would leave the order exactly as it is.");

        return Ready(
            SpotifyPlanAction.ReorderItems,
            [TargetOf(target)],
            [new SpotifyPlanStep(0, SpotifyPlanStepKind.ReorderItems, target.Playlist.Id, target.Playlist.Name,
                RangeStart: rangeStart, InsertBefore: insertBefore, RangeLength: length)],
            new SpotifyPlanPreview(
                $"Move {length} {Plural(length, "track", "tracks")} in “{target.Playlist.Name}”",
                "Reorder them",
                [$"Move {Plural(length, "the track", $"{length} tracks")} at position {rangeStart + 1} "
                 + $"to position {insertBefore + 1}"],
                [],
                RequiresHighImpactAcknowledgement: false,
                PlaylistsAffected: 1),
            nowUtc,
            originalRequest);
    }

    // ─── shared ──────────────────────────────────────────────────────────────

    private static Result Ready(
        SpotifyPlanAction action,
        IReadOnlyList<SpotifyPlanTarget> targets,
        IReadOnlyList<SpotifyPlanStep> steps,
        SpotifyPlanPreview preview,
        DateTimeOffset nowUtc,
        string? originalRequest,
        string? sourceDraftId = null)
    {
        if (targets.Count > MaxPlaylistsPerPlan)
        {
            return Result.Refuse(
                $"That would touch {targets.Count} playlists at once. I cap a single plan at "
                + $"{MaxPlaylistsPerPlan} so each batch stays reviewable — split it up.");
        }

        var plan = SpotifyPlanStateMachine.Create(action, targets, nowUtc) with
        {
            Steps = steps,
            Preview = preview,
            OriginalRequest = originalRequest,
            SourceDraftId = sourceDraftId
        };

        return new Result(plan, null);
    }

    /// <summary>
    /// Refuses anything we cannot see the current contents of. Building an edit on
    /// an unread playlist means the preview would be a guess, and the restore
    /// manifest would be empty — no undo, and no honest diff.
    /// </summary>
    private static string? Unwritable(SpotifyPlaylistContents target) =>
        !target.IsReadable
            ? $"Spotify will not let me read the contents of “{target.Playlist.Name}”, so I cannot "
              + "show you what would change or offer a way back. I will not edit it blind."
            : !target.Playlist.IsOwnedByUser && !target.Playlist.IsCollaborative
                ? NotYours(target.Playlist.Name)
                : null;

    private static string NotYours(string name) =>
        $"“{name}” is not yours to change — you follow it, but do not own or collaborate on it.";

    private static string TooManyItems(int count) =>
        $"That is {count} item changes in one go. I cap a plan at {MaxItemMutationsPerPlan} so a "
        + "failure part-way stays comprehensible — split it into smaller batches.";

    private static SpotifyPlanTarget TargetOf(SpotifyPlaylistContents contents) =>
        new(contents.Playlist.Id, contents.Playlist.Name, contents.SnapshotId ?? contents.Playlist.SnapshotId);

    private static SpotifyPlanTarget TargetOf(SpotifyPlaylistDto playlist) =>
        new(playlist.Id, playlist.Name, playlist.SnapshotId);

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..57] + "…";

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
