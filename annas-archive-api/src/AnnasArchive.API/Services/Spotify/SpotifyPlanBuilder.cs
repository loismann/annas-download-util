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

    // ─── phase 8: bulk cleanup and merge ─────────────────────────────────────

    /// <summary>
    /// Combine several playlists into one.
    ///
    /// Three deliberate choices. The target is populated and then *verified* before
    /// any source is touched, so a merge can never remove the originals when the
    /// copy did not land. Sources are left in the library unless removal is asked
    /// for. And nothing is silently dropped: exact URI repeats collapse, but two
    /// different recordings of the same song are counted and reported, because
    /// deciding they are "the same" is a judgement only the listener can make.
    /// </summary>
    public static Result Merge(
        IReadOnlyList<SpotifyPlaylistContents> sources,
        SpotifyPlaylistContents? existingTarget,
        string? newTargetName,
        bool isPublic,
        bool removeSources,
        DateTimeOffset nowUtc,
        string? originalRequest = null)
    {
        if (sources.Count < 2)
            return Result.Refuse("Merging needs at least two playlists. Which ones did you mean?");

        // A partial view is the one thing a merge must not be built on: items we
        // cannot see would be quietly left behind, and if the sources were then
        // removed they would be gone from the library with no copy anywhere.
        var unreadable = sources.Where(s => !s.IsReadable).Select(s => s.Playlist.Name).ToList();
        if (unreadable.Count > 0)
        {
            return Result.Refuse(
                $"Spotify will not let me read {Join(unreadable)} all the way through, so I cannot "
                + "promise the merged playlist would contain everything. I will not merge a partial view.");
        }

        if (existingTarget is not null && Unwritable(existingTarget) is { } targetRefusal)
            return Result.Refuse(targetRefusal);

        var targetName = string.IsNullOrWhiteSpace(newTargetName)
            ? existingTarget?.Playlist.Name
            : newTargetName.Trim();

        if (existingTarget is null && string.IsNullOrWhiteSpace(targetName))
            return Result.Refuse("What should I call the merged playlist?");

        // Duplicate sources would double-count everything and, worse, put the same
        // playlist on a removal list twice.
        var distinctSources = sources
            .GroupBy(s => s.Playlist.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (existingTarget is not null && distinctSources.Any(s => s.Playlist.Id == existingTarget.Playlist.Id))
        {
            return Result.Refuse(
                $"“{existingTarget.Playlist.Name}” is both the destination and one of the sources. "
                + "Pick a different destination, or leave it out of the list.");
        }

        var alreadyThere = (existingTarget?.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
            .Select(i => i.Uri!)
            .ToHashSet(StringComparer.Ordinal);

        // First-encountered ordering, per the merge policy: source order is the one
        // piece of the user's original curation we can preserve for free.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var toAdd = new List<string>();
        var contributed = new List<SpotifyPlaylistItemDto>();
        var localItems = new List<string>();
        var alreadyPresent = 0;
        var exactRepeats = 0;

        foreach (var item in distinctSources.SelectMany(s => s.Items))
        {
            if (item.Kind == SpotifyItemKind.Local || string.IsNullOrWhiteSpace(item.Uri))
            {
                localItems.Add(item.Name ?? "an unnamed local file");
                continue;
            }

            if (!seen.Add(item.Uri))
            {
                // The same song in two of the sources. Collapsing exact URI matches
                // is a fact, not a judgement, so it needs no review.
                exactRepeats++;
                continue;
            }

            contributed.Add(item);

            if (alreadyThere.Contains(item.Uri))
                alreadyPresent++;
            else
                toAdd.Add(item.Uri);
        }

        if (toAdd.Count == 0)
        {
            return Result.Refuse(existingTarget is null
                ? "Those playlists have nothing in them I can add to a merged playlist."
                : $"Everything in those playlists is already in “{existingTarget.Playlist.Name}”, "
                  + "so merging would not change it.");
        }

        if (toAdd.Count > MaxItemMutationsPerPlan)
            return Result.Refuse(TooManyItems(toAdd.Count));

        var expectedTotal = alreadyThere.Count + toAdd.Count;
        var steps = new List<SpotifyPlanStep>();
        var ordinal = 0;
        var targetId = existingTarget?.Playlist.Id;

        if (existingTarget is null)
        {
            steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.CreatePlaylist, null, targetName,
                Name: targetName, IsPublic: isPublic,
                Description: $"Merged from {Join(distinctSources.Select(s => s.Playlist.Name).ToList())}."));
        }

        steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.AddItems, targetId, targetName, Uris: toAdd));

        // The gate. Without this, "the copy worked" is an assumption, and the source
        // removal below would be acting on it.
        steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.VerifyPlaylistPopulated,
            targetId, targetName, ExpectedItemCount: expectedTotal));

        if (removeSources)
        {
            // One step per playlist, so a failure part-way removes strictly fewer —
            // never more — and the audit trail names exactly which came off.
            foreach (var source in distinctSources)
            {
                steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.RemoveFromLibrary,
                    source.Playlist.Id, source.Playlist.Name, Uris: [PlaylistUri(source.Playlist.Id)]));
            }
        }

        var effects = new List<string>
        {
            existingTarget is null
                ? $"Create a new {(isPublic ? "public" : "private")} playlist called “{targetName}”"
                : $"Add to your existing playlist “{targetName}”"
        };

        effects.AddRange(distinctSources.Select(s =>
            $"Take {s.Items.Count} {Plural(s.Items.Count, "item", "items")} from “{s.Playlist.Name}”"));

        effects.Add($"“{targetName}” ends up with {expectedTotal} {Plural(expectedTotal, "item", "items")}");
        effects.Add($"Check “{targetName}” really holds them before doing anything else");

        if (removeSources)
        {
            effects.AddRange(distinctSources.Select(s =>
                $"Remove “{s.Playlist.Name}” from your library"));
        }
        else
        {
            effects.Add($"Leave all {distinctSources.Count} original playlists exactly as they are");
        }

        var warnings = new List<string>();

        if (alreadyPresent > 0)
        {
            warnings.Add($"{alreadyPresent} {Plural(alreadyPresent, "track is", "tracks are")} already in "
                       + $"“{targetName}” and will not be added twice.");
        }

        if (exactRepeats > 0)
        {
            warnings.Add($"{exactRepeats} exact {Plural(exactRepeats, "repeat", "repeats")} across the source "
                       + "playlists will be collapsed into one copy.");
        }

        var probable = CountProbableRecordingRepeats(contributed);
        if (probable > 0)
        {
            warnings.Add($"{probable} {Plural(probable, "track looks", "tracks look")} like another recording of "
                       + "a song already in the merge — a live cut or a remaster, say. I am keeping both rather "
                       + "than deciding for you.");
        }

        if (localItems.Count > 0)
        {
            warnings.Add($"{localItems.Count} local {Plural(localItems.Count, "file", "files")} "
                       + $"({Join(localItems.Take(3).ToList())}{(localItems.Count > 3 ? ", …" : "")}) cannot be "
                       + "added through the API and will not make it into the merged playlist.");
        }

        if (removeSources)
        {
            warnings.Add("Removing a playlist from your library is an unfollow, not a delete — Spotify has no "
                       + "delete. Anyone else following these keeps them, and undo can re-follow them.");
            if (localItems.Count > 0)
            {
                warnings.Add("Those local files exist only in the source playlists. Removing the sources means "
                           + "losing the only reference to them.");
            }
        }

        var targets = new List<SpotifyPlanTarget>();
        if (existingTarget is not null) targets.Add(TargetOf(existingTarget));
        targets.AddRange(distinctSources.Select(TargetOf));

        return Ready(
            SpotifyPlanAction.MergePlaylists,
            targets,
            steps,
            new SpotifyPlanPreview(
                $"Merge {distinctSources.Count} playlists into “{targetName}” "
                + $"({toAdd.Count} {Plural(toAdd.Count, "track", "tracks")} to add)",
                removeSources ? "Merge and remove the originals" : "Merge them",
                effects, warnings,
                RequiresHighImpactAcknowledgement: true,
                ItemsAdded: toAdd.Count,
                ItemsSkippedAsDuplicates: alreadyPresent + exactRepeats,
                PlaylistsAffected: targets.Count + (existingTarget is null ? 1 : 0)),
            nowUtc,
            originalRequest);
    }

    /// <summary>
    /// Remove playlists from the library. This is an unfollow — Spotify has no delete
    /// operation at all — and every sentence here says so.
    ///
    /// Unreadable playlists are refused outright. "I cannot see inside it" is not
    /// evidence that it is empty, and a removal list is exactly where that confusion
    /// would cost something.
    /// </summary>
    public static Result RemoveFromLibrary(
        IReadOnlyList<SpotifyPlaylistContents> targets, DateTimeOffset nowUtc, string? originalRequest = null)
    {
        if (targets.Count == 0)
            return Result.Refuse("Which playlists should I take out of your library?");

        var distinct = targets
            .GroupBy(t => t.Playlist.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var unreadable = distinct.Where(t => !t.IsReadable).Select(t => t.Playlist.Name).ToList();
        if (unreadable.Count > 0)
        {
            return Result.Refuse(
                $"I cannot read what is inside {Join(unreadable)}, so I will not put "
                + $"{Plural(unreadable.Count, "it", "them")} on a removal list. Unreadable is unknown, not empty — "
                + "if you are sure, remove those in Spotify itself where you can see them first.");
        }

        var steps = distinct
            .Select((target, index) => new SpotifyPlanStep(
                index, SpotifyPlanStepKind.RemoveFromLibrary, target.Playlist.Id, target.Playlist.Name,
                Uris: [PlaylistUri(target.Playlist.Id)]))
            .ToList();

        var effects = distinct
            .Select(t => $"Remove “{t.Playlist.Name}” ({t.Items.Count} {Plural(t.Items.Count, "item", "items")}) "
                       + "from your library")
            .ToList();

        var warnings = new List<string>
        {
            "This is an unfollow, not a delete. Spotify has no way to delete a playlist: anyone else who "
            + "follows these keeps them, and yours can be re-followed by undoing this."
        };

        var withContent = distinct.Where(t => t.Items.Count > 0).ToList();
        if (withContent.Count > 0)
        {
            var total = withContent.Sum(t => t.Items.Count);
            warnings.Add($"{withContent.Count} of these {Plural(withContent.Count, "is", "are")} not empty — "
                       + $"{total} {Plural(total, "item", "items")} in total across "
                       + $"{Join(withContent.Select(t => t.Playlist.Name).ToList())}.");
        }

        var notOwned = distinct.Where(t => !t.Playlist.IsOwnedByUser).Select(t => t.Playlist.Name).ToList();
        if (notOwned.Count > 0)
        {
            warnings.Add($"You do not own {Join(notOwned)} — removing "
                       + $"{Plural(notOwned.Count, "it", "them")} only stops you following "
                       + $"{Plural(notOwned.Count, "it", "them")}.");
        }

        return Ready(
            SpotifyPlanAction.RemovePlaylistsFromLibrary,
            distinct.Select(TargetOf).ToList(),
            steps,
            new SpotifyPlanPreview(
                $"Remove {distinct.Count} {Plural(distinct.Count, "playlist", "playlists")} from your library",
                $"Remove {Plural(distinct.Count, "it", "them")}",
                effects, warnings,
                RequiresHighImpactAcknowledgement: true,
                PlaylistsAffected: distinct.Count),
            nowUtc,
            originalRequest);
    }

    /// <summary>
    /// Distinct Spotify URIs that nonetheless look like the same recording. Reported,
    /// never collapsed — see <see cref="SpotifyAnalysis"/> for why a "probable"
    /// duplicate is evidence rather than a decision.
    /// </summary>
    private static int CountProbableRecordingRepeats(IReadOnlyList<SpotifyPlaylistItemDto> items) =>
        items
            .Where(i => i.Kind == SpotifyItemKind.Track && !string.IsNullOrWhiteSpace(i.Name))
            .GroupBy(SpotifyAnalysis.RecordingKey, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 1 && g.Count() > 1)
            .Sum(g => g.Count() - 1);

    public static string PlaylistUri(string playlistId) => $"spotify:playlist:{playlistId}";

    /// <summary>"A", "A and B", "A, B and C" — plain English, not a bracketed list.</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => $"“{names[0]}”",
        _ => string.Join(", ", names.Take(names.Count - 1).Select(n => $"“{n}”"))
             + $" and “{names[^1]}”"
    };

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
