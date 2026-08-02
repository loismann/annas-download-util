using System.Net;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyPlanExecutor
{
    Task<SpotifyChangePlan> ConfirmAndExecuteAsync(
        string ownerKey, Guid planId, string confirmedBy, bool highImpactAcknowledged,
        CancellationToken token = default);

    Task<SpotifyChangePlan> BuildUndoAsync(
        string ownerKey, Guid planId, CancellationToken token = default);

    /// <summary>
    /// Picks a stalled plan back up at its first unfinished step. Steps that already
    /// succeeded are never re-run.
    /// </summary>
    Task<SpotifyChangePlan> ResumeAsync(
        string ownerKey, Guid planId, string confirmedBy, CancellationToken token = default);
}

/// <summary>
/// Executes a confirmed plan, in order, once.
///
/// Three rules do the real work here. Steps run in order and stop at the first
/// failure, so a plan can never do its cleanup half without its setup half.
/// Snapshots are revalidated immediately before the first write, so a plan built
/// against a playlist that has since changed is expired rather than applied to
/// something the user never reviewed. And a restore manifest is captured *before*
/// each destructive step, because afterwards the information needed to undo it is
/// gone.
/// </summary>
public sealed class SpotifyPlanExecutor : ISpotifyPlanExecutor
{
    private readonly ISpotifyService _spotify;
    private readonly ISpotifyInventoryService _inventory;
    private readonly ISpotifyPlanStore _plans;
    private readonly ISpotifyAuditService _audit;
    private readonly ISpotifyAccessTokenProvider _tokens;
    private readonly TimeProvider _time;

    public SpotifyPlanExecutor(
        ISpotifyService spotify,
        ISpotifyInventoryService inventory,
        ISpotifyPlanStore plans,
        ISpotifyAuditService audit,
        ISpotifyAccessTokenProvider tokens,
        TimeProvider time)
    {
        _spotify = spotify;
        _inventory = inventory;
        _plans = plans;
        _audit = audit;
        _tokens = tokens;
        _time = time;
    }

    public async Task<SpotifyChangePlan> ConfirmAndExecuteAsync(
        string ownerKey, Guid planId, string confirmedBy, bool highImpactAcknowledged,
        CancellationToken token = default)
    {
        var plan = _plans.Get(ownerKey, planId)
            ?? throw new InvalidOperationException("That plan no longer exists.");

        // Idempotency. A double tap, a retried request, or an impatient refresh must
        // not run the same writes twice; a finished plan simply reports itself.
        if (plan.Status is SpotifyPlanStatus.Completed or SpotifyPlanStatus.PartiallyCompleted
            or SpotifyPlanStatus.Failed or SpotifyPlanStatus.Executing)
        {
            return plan;
        }

        var now = _time.GetUtcNow();

        if (plan.IsExpired(now))
        {
            plan = Persist(ownerKey, SpotifyPlanStateMachine.Expire(plan, now), SpotifyAuditEventKind.PlanExpired,
                confirmedBy, "Plan expired before it was confirmed.");
            throw new InvalidOperationException(
                "That plan expired. Ask again and I will rebuild it against the current playlist.");
        }

        if (plan.Preview?.RequiresHighImpactAcknowledgement == true && !highImpactAcknowledged)
        {
            throw new InvalidOperationException(
                "This one discards content, so it needs the high-impact confirmation as well.");
        }

        await EnsureTargetsUnchangedAsync(ownerKey, plan, confirmedBy, token);

        if (plan.Status == SpotifyPlanStatus.Draft)
            plan = SpotifyPlanStateMachine.MarkAwaitingConfirmation(plan, now);

        plan = SpotifyPlanStateMachine.Confirm(plan, confirmedBy, now);
        plan = Persist(ownerKey, plan, SpotifyAuditEventKind.PlanConfirmed, confirmedBy,
            $"Confirmed {plan.Action} affecting {plan.Targets.Count} playlist(s).");

        return await RunAsync(ownerKey, plan, confirmedBy, token);
    }

    /// <summary>
    /// Resumes a plan that stopped part-way.
    ///
    /// Deliberately narrow. It re-runs only steps that did not succeed, in the
    /// original order, and it does not re-check the plan's recorded snapshots —
    /// those are guaranteed stale, because our own successful steps moved them.
    /// Safety comes from each step revalidating what it needs instead: an add
    /// re-reads the playlist and skips what is already there, and the merge's
    /// verify step still stands between population and any source removal.
    /// </summary>
    public async Task<SpotifyChangePlan> ResumeAsync(
        string ownerKey, Guid planId, string confirmedBy, CancellationToken token = default)
    {
        var plan = _plans.Get(ownerKey, planId)
            ?? throw new InvalidOperationException("That plan no longer exists.");

        if (plan.Status is not (SpotifyPlanStatus.PartiallyCompleted or SpotifyPlanStatus.Failed))
        {
            throw new InvalidOperationException(
                "Only a plan that stopped part-way can be picked back up.");
        }

        var remaining = plan.OrderedSteps.Count(s => s.Status != SpotifyPlanStepStatus.Succeeded);
        if (remaining == 0)
            throw new InvalidOperationException("Every step of that plan already succeeded.");

        // Clear the previous failure text so a second stop reports its own reason
        // rather than the first one.
        var reset = plan.OrderedSteps
            .Select(step => step.Status == SpotifyPlanStepStatus.Succeeded
                ? step
                : step with { Status = SpotifyPlanStepStatus.Pending, Failure = null })
            .ToList();

        plan = SpotifyPlanStateMachine.Resume(plan) with { Steps = reset, Failure = null };
        plan = Persist(ownerKey, plan, SpotifyAuditEventKind.PlanResumed, confirmedBy,
            $"Resuming {remaining} unfinished step(s).");

        return await RunAsync(ownerKey, plan, confirmedBy, token);
    }

    /// <summary>
    /// Builds the inverse of a completed plan. It is a *new* plan needing its own
    /// confirmation — undo is a change like any other, and quietly reversing writes
    /// without review would be the same mistake in the opposite direction.
    /// </summary>
    public async Task<SpotifyChangePlan> BuildUndoAsync(
        string ownerKey, Guid planId, CancellationToken token = default)
    {
        var original = _plans.Get(ownerKey, planId)
            ?? throw new InvalidOperationException("That plan no longer exists.");

        if (!original.CanUndo)
        {
            throw new InvalidOperationException(original.UndoneByPlanId is not null
                ? "That change has already been undone."
                : "There is nothing recorded that would let me undo that.");
        }

        var now = _time.GetUtcNow();
        var steps = new List<SpotifyPlanStep>();
        var effects = new List<string>();
        var warnings = new List<string>();
        var targets = new List<SpotifyPlanTarget>();
        var ordinal = 0;

        foreach (var manifest in original.RestoreManifests!)
        {
            // The inverse of a library removal is a re-follow. Handle it before the
            // read below, because a removed playlist is exactly the case where the
            // library no longer lists it.
            if (manifest.RemovedLibraryUri is { } removedUri)
            {
                steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.AddToLibrary,
                    manifest.PlaylistId, manifest.PlaylistName, Uris: [removedUri]));
                effects.Add($"Put “{manifest.PlaylistName}” back in your library");
                continue;
            }

            // The inverse of creating a playlist is removing it again — Spotify has
            // no delete, so this unfollows the thing the plan brought into being.
            if (manifest.WasCreated)
            {
                steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.RemoveFromLibrary,
                    manifest.PlaylistId, manifest.PlaylistName,
                    Uris: [SpotifyPlanBuilder.PlaylistUri(manifest.PlaylistId)]));
                effects.Add($"Remove “{manifest.PlaylistName}” — the playlist that plan created — "
                          + "from your library again");
                warnings.Add($"“{manifest.PlaylistName}” is not deleted, only unfollowed. Spotify keeps it "
                           + "recoverable for a while.");
                continue;
            }

            // Re-read rather than trusting the manifest's snapshot: if the playlist
            // moved again since, the user must see that before restoring over it.
            var current = await _spotify.GetPlaylistAsync(manifest.PlaylistId, token);
            if (current is null)
            {
                warnings.Add($"“{manifest.PlaylistName}” no longer exists, so it cannot be restored.");
                continue;
            }

            if (current.SnapshotId is not null && manifest.SnapshotId is not null
                && current.SnapshotId != manifest.SnapshotId)
            {
                warnings.Add($"“{manifest.PlaylistName}” has changed again since. Restoring will "
                           + "overwrite those later changes too.");
            }

            targets.Add(new SpotifyPlanTarget(manifest.PlaylistId, manifest.PlaylistName, current.SnapshotId));

            if (manifest.PreviousName is not null || manifest.PreviousDescription is not null
                || manifest.PreviousIsPublic is not null)
            {
                steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.ChangeDetails,
                    manifest.PlaylistId, manifest.PlaylistName,
                    Name: manifest.PreviousName, Description: manifest.PreviousDescription,
                    IsPublic: manifest.PreviousIsPublic));
                effects.Add($"Restore the previous details of “{manifest.PlaylistName}”");
            }

            if (manifest.OrderedUris.Count > 0 || original.Action is SpotifyPlanAction.ReplaceItems)
            {
                steps.Add(new SpotifyPlanStep(ordinal++, SpotifyPlanStepKind.ReplaceItems,
                    manifest.PlaylistId, manifest.PlaylistName, Uris: manifest.OrderedUris));
                effects.Add($"Put back the {manifest.OrderedUris.Count} item(s) “{manifest.PlaylistName}” "
                          + "had, in their original order");
            }

            if (manifest.UnrestorableItems is { Count: > 0 })
            {
                warnings.Add($"{manifest.UnrestorableItems.Count} item(s) in “{manifest.PlaylistName}” "
                           + "cannot be re-added through the API and will stay missing.");
            }
        }

        if (steps.Count == 0)
            throw new InvalidOperationException("Nothing about that change can be put back.");

        var undo = SpotifyPlanStateMachine.Create(SpotifyPlanAction.RestorePreviousChange, targets, now) with
        {
            Steps = steps,
            UndoOfPlanId = original.Id,
            OriginalRequest = $"Undo of {original.Action}",
            Preview = new SpotifyPlanPreview(
                $"Undo the earlier {original.Action}",
                "Undo it",
                effects,
                warnings,
                RequiresHighImpactAcknowledgement: true,
                PlaylistsAffected: targets.Count)
        };

        _plans.Save(ownerKey, undo);
        Audit(ownerKey, undo, SpotifyAuditEventKind.PlanBuilt, null, $"Undo built for plan {original.Id}.");
        return undo;
    }

    // ─── execution ───────────────────────────────────────────────────────────

    private async Task<SpotifyChangePlan> RunAsync(
        string ownerKey, SpotifyChangePlan plan, string confirmedBy, CancellationToken token)
    {
        var steps = plan.OrderedSteps.ToList();
        var manifests = new List<SpotifyRestoreManifest>(plan.RestoreManifests ?? []);
        string? failure = null;

        // A resumed plan carries the ID of a playlist an earlier attempt created, so
        // a re-run populates that playlist rather than making a second one.
        var createdPlaylistId = steps
            .FirstOrDefault(s => s.CreatedPlaylistId is not null)?.CreatedPlaylistId;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // Already done on an earlier attempt. Re-running it is the one thing a
            // resume must never do.
            if (step.Status == SpotifyPlanStepStatus.Succeeded)
                continue;

            if (failure is not null)
            {
                // Everything after a failure is skipped, not attempted. This is what
                // stops a merge removing its sources when the target never filled.
                steps[i] = step with { Status = SpotifyPlanStepStatus.Skipped };
                continue;
            }

            try
            {
                var (executed, newPlaylistId, manifest) =
                    await ExecuteStepAsync(step, createdPlaylistId, token);

                createdPlaylistId ??= newPlaylistId;
                if (manifest is not null) manifests.Add(manifest);

                steps[i] = executed;
                Audit(ownerKey, plan, SpotifyAuditEventKind.StepSucceeded, confirmedBy,
                    $"Step {step.Ordinal} ({step.Kind}) on {executed.PlaylistName ?? executed.PlaylistId}.");
            }
            catch (Exception ex) when (ex is SpotifyApiException or SpotifyConnectionException or InvalidOperationException)
            {
                failure = Describe(ex);
                steps[i] = step with { Status = SpotifyPlanStepStatus.Failed, Failure = failure };
                Log.Warning(ex, "[Spotify] Plan {PlanId} step {Ordinal} failed", plan.Id, step.Ordinal);
                Audit(ownerKey, plan, SpotifyAuditEventKind.StepFailed, confirmedBy,
                    $"Step {step.Ordinal} ({step.Kind}) failed: {failure}");
            }

            // Persist after every step, not only at the end. A bulk plan is long
            // enough to watch, and a process that dies mid-run must leave a record
            // of what it had already done rather than looking untouched.
            _plans.Save(ownerKey, plan with { Steps = steps, RestoreManifests = manifests });
        }

        var succeeded = steps.Count(s => s.Status == SpotifyPlanStepStatus.Succeeded);
        plan = plan with { Steps = steps, RestoreManifests = manifests };

        if (failure is null)
        {
            return Persist(ownerKey, SpotifyPlanStateMachine.Complete(plan),
                SpotifyAuditEventKind.PlanCompleted, confirmedBy,
                $"All {steps.Count} step(s) completed.");
        }

        // Partial matters: some writes landed and the user needs to know which, not
        // just that "it failed".
        return succeeded > 0
            ? Persist(ownerKey, SpotifyPlanStateMachine.CompletePartially(plan, failure),
                SpotifyAuditEventKind.PlanPartiallyCompleted, confirmedBy,
                $"{succeeded} of {steps.Count} step(s) completed before: {failure}")
            : Persist(ownerKey, SpotifyPlanStateMachine.Fail(plan, failure),
                SpotifyAuditEventKind.PlanFailed, confirmedBy, failure);
    }

    private async Task<(SpotifyPlanStep Step, string? CreatedPlaylistId, SpotifyRestoreManifest? Manifest)>
        ExecuteStepAsync(SpotifyPlanStep step, string? createdPlaylistId, CancellationToken token)
    {
        // A step with no playlist ID belongs to a playlist an earlier step created.
        var playlistId = step.PlaylistId ?? createdPlaylistId;

        switch (step.Kind)
        {
            case SpotifyPlanStepKind.CreatePlaylist:
            {
                var created = await _spotify.CreatePlaylistAsync(
                    step.Name!, step.Description, step.IsPublic ?? false, token);

                // Undoing a creation means unfollowing what we just made. Recording
                // it here is what makes an unwanted playlist removable without the
                // user having to go find it in Spotify.
                var manifest = new SpotifyRestoreManifest(
                    created.Id, created.Name, created.SnapshotId, [], WasCreated: true);

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    CreatedPlaylistId = created.Id,
                    PlaylistId = created.Id,
                    ResultingSnapshotId = created.SnapshotId
                }, created.Id, manifest);
            }

            case SpotifyPlanStepKind.AddItems:
            {
                Require(playlistId, step);

                // Add only what is not already there. This matches what the builder
                // previewed — it refuses to add existing URIs — and it is what makes
                // re-running a half-finished add safe rather than duplicating tracks.
                var wanted = await FilterOutItemsAlreadyPresentAsync(
                    playlistId!, step.Uris ?? [], token);

                var snapshot = wanted.Count > 0
                    ? await _spotify.AddItemsAsync(playlistId!, wanted, token)
                    : null;

                // Undoing an add means removing exactly what we added.
                var manifest = new SpotifyRestoreManifest(
                    playlistId!, step.PlaylistName ?? playlistId!, snapshot, [],
                    UnrestorableItems: null);

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    PlaylistId = playlistId,
                    ResultingSnapshotId = snapshot
                }, null, step.PlaylistId is null ? null : manifest);
            }

            case SpotifyPlanStepKind.VerifyPlaylistPopulated:
            {
                Require(playlistId, step);

                var playlist = await _spotify.GetPlaylistAsync(playlistId!, token)
                    ?? throw new InvalidOperationException(
                        $"“{step.PlaylistName ?? playlistId}” could not be read back, so I cannot confirm "
                        + "the merge landed. Nothing further will run.");

                var contents = await _inventory.GetContentsAsync(playlist, token);
                var expected = step.ExpectedItemCount ?? 0;

                if (!contents.IsReadable)
                {
                    throw new InvalidOperationException(
                        $"Spotify would not let me read “{playlist.Name}” back, so I cannot confirm it holds "
                        + $"the {expected} items before going any further. Nothing else has been touched.");
                }

                if (contents.Items.Count < expected)
                {
                    throw new InvalidOperationException(
                        $"“{playlist.Name}” has {contents.Items.Count} items but should have {expected}. "
                        + "Stopping here — the original playlists have not been touched.");
                }

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    PlaylistId = playlistId,
                    ResultingSnapshotId = contents.SnapshotId
                }, null, null);
            }

            case SpotifyPlanStepKind.RemoveFromLibrary:
            {
                Require(playlistId, step);
                var uris = (step.Uris ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

                if (uris.Count == 0)
                    throw new InvalidOperationException($"Step {step.Ordinal} has no playlist URI to remove.");

                // Record the way back before removing. Spotify keeps the playlist —
                // this is an unfollow — so re-following it is a real undo.
                var manifest = new SpotifyRestoreManifest(
                    playlistId!, step.PlaylistName ?? playlistId!, null, [],
                    RemovedLibraryUri: uris[0]);

                await _spotify.RemovePlaylistsFromLibraryAsync(uris, token);

                return (step with { Status = SpotifyPlanStepStatus.Succeeded, PlaylistId = playlistId },
                    null, manifest);
            }

            case SpotifyPlanStepKind.AddToLibrary:
            {
                Require(playlistId, step);
                var uris = (step.Uris ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

                if (uris.Count == 0)
                    throw new InvalidOperationException($"Step {step.Ordinal} has no playlist URI to restore.");

                await _spotify.AddPlaylistsToLibraryAsync(uris, token);

                return (step with { Status = SpotifyPlanStepStatus.Succeeded, PlaylistId = playlistId },
                    null, null);
            }

            case SpotifyPlanStepKind.RemoveItems:
            case SpotifyPlanStepKind.ReplaceItems:
            {
                Require(playlistId, step);

                // Capture what is there *before* touching it. After the write this
                // information does not exist anywhere.
                var before = await ReadForRestoreAsync(playlistId!, step.PlaylistName, token);

                var snapshot = step.Kind == SpotifyPlanStepKind.RemoveItems
                    ? await _spotify.RemoveItemsAsync(playlistId!, step.Uris ?? [], null, token)
                    : await _spotify.ReplaceItemsAsync(playlistId!, step.Uris ?? [], token);

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    PlaylistId = playlistId,
                    ResultingSnapshotId = snapshot
                }, null, before);
            }

            case SpotifyPlanStepKind.ReorderItems:
            {
                Require(playlistId, step);
                var before = await ReadForRestoreAsync(playlistId!, step.PlaylistName, token);

                var snapshot = await _spotify.ReorderItemsAsync(
                    playlistId!, step.RangeStart ?? 0, step.InsertBefore ?? 0, step.RangeLength ?? 1, null, token);

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    PlaylistId = playlistId,
                    ResultingSnapshotId = snapshot
                }, null, before);
            }

            case SpotifyPlanStepKind.ChangeDetails:
            {
                Require(playlistId, step);
                var current = await _spotify.GetPlaylistAsync(playlistId!, token);

                await _spotify.ChangePlaylistDetailsAsync(
                    playlistId!, step.Name, step.Description, step.IsPublic, token);

                var manifest = current is null ? null : new SpotifyRestoreManifest(
                    playlistId!, current.Name, current.SnapshotId, [],
                    PreviousName: current.Name,
                    PreviousIsPublic: current.IsPublic);

                return (step with { Status = SpotifyPlanStepStatus.Succeeded, PlaylistId = playlistId },
                    null, manifest);
            }

            default:
                throw new InvalidOperationException($"{step.Kind} is not executable yet.");
        }
    }

    /// <summary>
    /// Drops URIs the playlist already holds.
    ///
    /// A newly created playlist is empty and this costs one read, which is cheap
    /// beside the alternative: an add that ran, timed out on the response, and got
    /// retried would otherwise put every track in twice.
    /// </summary>
    private async Task<IReadOnlyList<string>> FilterOutItemsAlreadyPresentAsync(
        string playlistId, IReadOnlyList<string> uris, CancellationToken token)
    {
        var wanted = uris.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (wanted.Count == 0)
            return wanted;

        var playlist = await _spotify.GetPlaylistAsync(playlistId, token);
        if (playlist is null)
            return wanted;

        var contents = await _inventory.GetContentsAsync(playlist, token);

        // Only an authoritative read may be used to skip an add. If Spotify would
        // not show us the contents, adding the full list is the safer error.
        if (!contents.IsReadable)
            return wanted;

        var present = contents.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Uri))
            .Select(i => i.Uri!)
            .ToHashSet(StringComparer.Ordinal);

        return wanted.Where(uri => !present.Contains(uri)).ToList();
    }

    /// <summary>
    /// The ordered URI list a restore would put back, plus a count of what it could
    /// not. Local files have no catalog URI, so they are lost to any API-based undo
    /// and the manifest says so rather than pretending otherwise.
    /// </summary>
    private async Task<SpotifyRestoreManifest> ReadForRestoreAsync(
        string playlistId, string? playlistName, CancellationToken token)
    {
        var playlist = await _spotify.GetPlaylistAsync(playlistId, token);
        var contents = playlist is null
            ? null
            : await _inventory.GetContentsAsync(playlist, token);

        var items = contents?.Items ?? [];
        var restorable = items.Where(i => !string.IsNullOrWhiteSpace(i.Uri) && !i.IsLocal)
                              .Select(i => i.Uri!).ToList();
        var lost = items.Where(i => string.IsNullOrWhiteSpace(i.Uri) || i.IsLocal)
                        .Select(i => i.Name ?? "unnamed item").ToList();

        return new SpotifyRestoreManifest(
            playlistId,
            playlist?.Name ?? playlistName ?? playlistId,
            contents?.SnapshotId ?? playlist?.SnapshotId,
            restorable,
            UnrestorableItems: lost.Count > 0 ? lost : null);
    }

    /// <summary>
    /// The last gate before any write. A plan is reviewed against a specific version
    /// of a playlist; if that version has moved on, the diff the user approved no
    /// longer describes what would happen.
    /// </summary>
    private async Task EnsureTargetsUnchangedAsync(
        string ownerKey, SpotifyChangePlan plan, string confirmedBy, CancellationToken token)
    {
        foreach (var target in plan.Targets.Where(t => t.SnapshotId is not null))
        {
            var current = await _spotify.GetPlaylistAsync(target.PlaylistId, token);

            if (current is null)
            {
                Persist(ownerKey, SpotifyPlanStateMachine.Cancel(plan), SpotifyAuditEventKind.PlanCancelled,
                    confirmedBy, $"Target {target.DisplayName} no longer exists.");
                throw new InvalidOperationException(
                    $"“{target.DisplayName}” no longer exists, so I have cancelled that plan.");
            }

            if (current.SnapshotId is not null && current.SnapshotId != target.SnapshotId)
            {
                Persist(ownerKey, plan with { Status = SpotifyPlanStatus.Expired },
                    SpotifyAuditEventKind.PlanExpired, confirmedBy,
                    $"Snapshot for {target.DisplayName} changed before execution.");
                throw new InvalidOperationException(
                    $"“{target.DisplayName}” changed since I built that plan, so the preview you "
                    + "approved is out of date. Ask again and I will rebuild it.");
            }
        }
    }

    private static void Require(string? playlistId, SpotifyPlanStep step)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new InvalidOperationException($"Step {step.Ordinal} has no playlist to act on.");
    }

    private SpotifyChangePlan Persist(
        string ownerKey, SpotifyChangePlan plan, SpotifyAuditEventKind kind, string? actor, string detail)
    {
        _plans.Save(ownerKey, plan);
        Audit(ownerKey, plan, kind, actor, detail);
        return plan;
    }

    private void Audit(
        string ownerKey, SpotifyChangePlan plan, SpotifyAuditEventKind kind, string? actor, string detail)
    {
        string? account = null;
        try { account = _tokens.GetConnectedSpotifyUserId(); }
        catch (SpotifyConnectionException) { /* history is still worth writing without it */ }

        _audit.Record(ownerKey, new SpotifyAuditEvent(
            Guid.NewGuid(), plan.Id, kind, _time.GetUtcNow(), actor, account, detail));
    }

    private static string Describe(Exception ex) => ex switch
    {
        SpotifyApiException { SpotifyStatusCode: HttpStatusCode.Forbidden } =>
            "Spotify refused the change — that playlist is not yours to modify.",
        SpotifyApiException { SpotifyStatusCode: HttpStatusCode.NotFound } =>
            "Spotify could not find that playlist any more.",
        SpotifyApiException { SpotifyStatusCode: HttpStatusCode.TooManyRequests } =>
            "Spotify is rate limiting; the remaining steps were not attempted.",
        SpotifyApiException api => api.SpotifyMessage ?? "Spotify rejected the change.",
        SpotifyConnectionException connection => connection.Message,
        _ => ex.Message
    };
}
