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
        string? createdPlaylistId = null;
        string? failure = null;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

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

                return (step with
                {
                    Status = SpotifyPlanStepStatus.Succeeded,
                    CreatedPlaylistId = created.Id,
                    PlaylistId = created.Id,
                    ResultingSnapshotId = created.SnapshotId
                }, created.Id, null);
            }

            case SpotifyPlanStepKind.AddItems:
            {
                Require(playlistId, step);
                var snapshot = await _spotify.AddItemsAsync(playlistId!, step.Uris ?? [], token);

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
