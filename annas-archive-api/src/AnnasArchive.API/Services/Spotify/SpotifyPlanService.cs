using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyPlanService
{
    Task<SpotifyPlanBuilder.Result> BuildAsync(
        string ownerKey, SpotifyBuildPlanRequest request, CancellationToken token = default);

    SpotifyPlanDto? Get(string ownerKey, Guid planId);
    IReadOnlyList<SpotifyPlanDto> List(string ownerKey, int limit = 25);
    SpotifyPlanDto? Cancel(string ownerKey, Guid planId);
}

/// <summary>
/// Resolves a request into the concrete playlist and contents a plan needs, then
/// hands off to <see cref="SpotifyPlanBuilder"/> for the actual shaping.
///
/// The split matters: everything that touches Spotify lives here, and everything
/// that decides what a change means lives in the builder, which is a pure function
/// and therefore cheap to test exhaustively.
/// </summary>
public sealed class SpotifyPlanService : ISpotifyPlanService
{
    private readonly ISpotifyService _spotify;
    private readonly ISpotifyInventoryService _inventory;
    private readonly ISpotifyPlanStore _plans;
    private readonly ISpotifyAuditService _audit;
    private readonly ISpotifyDiscoveryStore _drafts;
    private readonly TimeProvider _time;

    public SpotifyPlanService(
        ISpotifyService spotify,
        ISpotifyInventoryService inventory,
        ISpotifyPlanStore plans,
        ISpotifyAuditService audit,
        ISpotifyDiscoveryStore drafts,
        TimeProvider time)
    {
        _spotify = spotify;
        _inventory = inventory;
        _plans = plans;
        _audit = audit;
        _drafts = drafts;
        _time = time;
    }

    public async Task<SpotifyPlanBuilder.Result> BuildAsync(
        string ownerKey, SpotifyBuildPlanRequest request, CancellationToken token = default)
    {
        var now = _time.GetUtcNow();

        var result = request.Action switch
        {
            SpotifyPlanAction.CreatePlaylist => BuildCreate(ownerKey, request, now),
            SpotifyPlanAction.AddItems => await WithContentsAsync(request,
                contents => SpotifyPlanBuilder.AddItems(contents, request.Uris ?? [], now, request.OriginalRequest),
                token),
            SpotifyPlanAction.RemoveItems => await WithContentsAsync(request,
                contents => SpotifyPlanBuilder.RemoveItems(contents, request.Uris ?? [], now, request.OriginalRequest),
                token),
            SpotifyPlanAction.ReplaceItems => await WithContentsAsync(request,
                contents => SpotifyPlanBuilder.ReplaceItems(contents, request.OrderedUris ?? [], now, request.OriginalRequest),
                token),
            SpotifyPlanAction.ReorderItems => await WithContentsAsync(request,
                contents => SpotifyPlanBuilder.ReorderItems(
                    contents, request.RangeStart ?? 0, request.InsertBefore ?? 0, request.RangeLength ?? 1,
                    now, request.OriginalRequest),
                token),
            SpotifyPlanAction.RenamePlaylist => await WithPlaylistAsync(request,
                playlist => SpotifyPlanBuilder.Rename(playlist, request.Name ?? "", now, request.OriginalRequest),
                token),
            SpotifyPlanAction.ChangePlaylistDetails => await WithPlaylistAsync(request,
                playlist => SpotifyPlanBuilder.ChangeDetails(
                    playlist, request.Name, request.Description, request.IsPublic, now, request.OriginalRequest),
                token),
            SpotifyPlanAction.MergePlaylists => await BuildMergeAsync(request, now, token),
            SpotifyPlanAction.RemovePlaylistsFromLibrary => await BuildLibraryRemovalAsync(request, now, token),
            _ => SpotifyPlanBuilder.Result.Refuse(
                $"{request.Action} is not something I can build a plan for.")
        };

        if (result.Plan is { } plan)
        {
            var awaiting = SpotifyPlanStateMachine.MarkAwaitingConfirmation(plan, now);
            _plans.Save(ownerKey, awaiting);
            _audit.Record(ownerKey, new SpotifyAuditEvent(
                Guid.NewGuid(), awaiting.Id, SpotifyAuditEventKind.PlanBuilt, now, null, null,
                awaiting.Preview?.Summary ?? awaiting.Action.ToString()));

            return new SpotifyPlanBuilder.Result(awaiting, null);
        }

        return result;
    }

    public SpotifyPlanDto? Get(string ownerKey, Guid planId) =>
        _plans.Get(ownerKey, planId) is { } plan ? ToDto(plan) : null;

    public IReadOnlyList<SpotifyPlanDto> List(string ownerKey, int limit = 25) =>
        _plans.List(ownerKey, limit).Select(ToDto).ToList();

    public SpotifyPlanDto? Cancel(string ownerKey, Guid planId)
    {
        var plan = _plans.Get(ownerKey, planId);
        if (plan is null) return null;

        var cancelled = SpotifyPlanStateMachine.Cancel(plan);
        _plans.Save(ownerKey, cancelled);
        _audit.Record(ownerKey, new SpotifyAuditEvent(
            Guid.NewGuid(), cancelled.Id, SpotifyAuditEventKind.PlanCancelled, _time.GetUtcNow(),
            null, null, "Cancelled before execution."));

        return ToDto(cancelled);
    }

    public static SpotifyPlanDto ToDto(SpotifyChangePlan plan) => new(
        plan.Id, plan.Action, plan.SafetyTier, plan.Status, plan.CreatedAtUtc, plan.ExpiresAtUtc,
        plan.Targets,
        plan.Preview ?? new SpotifyPlanPreview(plan.Action.ToString(), "Confirm", [], [], false),
        plan.OrderedSteps,
        plan.OriginalRequest, plan.ConfirmedBy, plan.ConfirmedAtUtc, plan.Failure,
        plan.CanUndo, plan.UndoOfPlanId,
        DescribeRecovery(plan));

    /// <summary>
    /// What a half-finished plan leaves the user able to do. Only computed for a plan
    /// that actually stopped part-way — offering "pick this back up" on a plan that
    /// completed would invite a second run of work that already landed.
    /// </summary>
    private static SpotifyPlanRecovery? DescribeRecovery(SpotifyChangePlan plan)
    {
        if (plan.Status is not (SpotifyPlanStatus.PartiallyCompleted or SpotifyPlanStatus.Failed))
            return null;

        var steps = plan.OrderedSteps;
        var succeeded = steps.Count(s => s.Status == SpotifyPlanStepStatus.Succeeded);
        var failed = steps.Count(s => s.Status == SpotifyPlanStepStatus.Failed);
        var skipped = steps.Count(s => s.Status is SpotifyPlanStepStatus.Skipped or SpotifyPlanStepStatus.Pending);
        var remaining = failed + skipped;

        var advice = remaining == 0
            ? "Every step finished, so there is nothing left to pick up."
            : succeeded == 0
                ? $"Nothing was changed. All {remaining} step(s) can be retried, or you can ask again "
                  + "and I will rebuild the plan against the library as it is now."
                : $"{succeeded} step(s) landed and {remaining} did not. Picking it back up re-runs only the "
                  + "unfinished ones — anything that already succeeded is left alone."
                  + (plan.CanUndo ? " You can also undo what did land." : "");

        return new SpotifyPlanRecovery(remaining > 0, succeeded, failed, skipped, advice);
    }

    // ─── resolution ──────────────────────────────────────────────────────────

    private SpotifyPlanBuilder.Result BuildCreate(
        string ownerKey, SpotifyBuildPlanRequest request, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.DraftId))
            return SpotifyPlanBuilder.Result.Refuse("Which draft should I create? Start one first.");

        var draft = _drafts.Get(ownerKey, request.DraftId);
        return draft is null
            ? SpotifyPlanBuilder.Result.Refuse("That draft has expired or does not exist.")
            : SpotifyPlanBuilder.CreateFromDraft(
                draft, request.Name, request.IsPublic ?? false, now, request.OriginalRequest);
    }

    // ─── phase 8: multi-playlist resolution ──────────────────────────────────

    /// <summary>
    /// Every source is resolved and read *before* the builder sees anything, so a
    /// merge is shaped from the real contents rather than from counts. An unresolved
    /// or ambiguous name stops the whole thing: a merge that quietly used four of the
    /// five playlists asked for would be the worst possible outcome here.
    /// </summary>
    private async Task<SpotifyPlanBuilder.Result> BuildMergeAsync(
        SpotifyBuildPlanRequest request, DateTimeOffset now, CancellationToken token)
    {
        var sources = await ResolveManyAsync(request, token);
        if (sources.Refusal is not null)
            return SpotifyPlanBuilder.Result.Refuse(sources.Refusal);

        SpotifyPlaylistContents? target = null;
        if (!string.IsNullOrWhiteSpace(request.TargetPlaylistId)
            || !string.IsNullOrWhiteSpace(request.TargetPlaylistReference))
        {
            var resolved = await ResolveAsync(
                request with
                {
                    PlaylistId = request.TargetPlaylistId,
                    PlaylistReference = request.TargetPlaylistReference
                },
                token);

            if (resolved.Refusal is not null)
                return SpotifyPlanBuilder.Result.Refuse(resolved.Refusal);

            target = await _inventory.GetContentsAsync(resolved.Playlist!, token);
        }

        var contents = await _inventory.GetAllContentsAsync(sources.Playlists, token);

        return SpotifyPlanBuilder.Merge(
            contents, target, request.Name, request.IsPublic ?? false, request.RemoveSources,
            now, request.OriginalRequest);
    }

    private async Task<SpotifyPlanBuilder.Result> BuildLibraryRemovalAsync(
        SpotifyBuildPlanRequest request, DateTimeOffset now, CancellationToken token)
    {
        var resolved = await ResolveManyAsync(request, token);
        if (resolved.Refusal is not null)
            return SpotifyPlanBuilder.Result.Refuse(resolved.Refusal);

        var contents = await _inventory.GetAllContentsAsync(resolved.Playlists, token);
        return SpotifyPlanBuilder.RemoveFromLibrary(contents, now, request.OriginalRequest);
    }

    /// <summary>
    /// Turns a list of IDs and/or names into playlists, refusing the whole request
    /// the moment one of them cannot be pinned down to exactly one playlist.
    /// </summary>
    private async Task<ManyResolution> ResolveManyAsync(
        SpotifyBuildPlanRequest request, CancellationToken token)
    {
        var ids = request.PlaylistIds ?? [];
        var references = request.PlaylistReferences ?? [];

        if (ids.Count == 0 && references.Count == 0)
            return new ManyResolution([], "Which playlists did you mean? Name them and I will look them up.");

        var playlists = new List<SpotifyPlaylistDto>();

        foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            var found = await _spotify.GetPlaylistAsync(id, token);
            if (found is null)
                return new ManyResolution([], "One of those playlists no longer exists, so I stopped there.");
            playlists.Add(found);
        }

        if (references.Count > 0)
        {
            var inventory = await _spotify.GetUserPlaylistsAsync(token);

            foreach (var reference in references.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var match = SpotifyPlaylistResolver.Resolve(reference, inventory);

                if (match.Kind == SpotifyPlaylistMatchKind.Ambiguous)
                {
                    return new ManyResolution([],
                        $"{match.Candidates.Count} playlists match “{reference}”. Tell me which one and "
                        + "I will build the plan against it — I will not guess when playlists are at stake.");
                }

                if (match.Kind != SpotifyPlaylistMatchKind.Resolved)
                    return new ManyResolution([], $"I could not find a playlist matching “{reference}”.");

                playlists.Add(match.Playlist!);
            }
        }

        return new ManyResolution(playlists, null);
    }

    private sealed record ManyResolution(IReadOnlyList<SpotifyPlaylistDto> Playlists, string? Refusal);

    private async Task<SpotifyPlanBuilder.Result> WithPlaylistAsync(
        SpotifyBuildPlanRequest request,
        Func<SpotifyPlaylistDto, SpotifyPlanBuilder.Result> build,
        CancellationToken token)
    {
        var resolution = await ResolveAsync(request, token);
        return resolution.Plan is not null || resolution.Refusal is not null
            ? resolution.AsResult()
            : build(resolution.Playlist!);
    }

    private async Task<SpotifyPlanBuilder.Result> WithContentsAsync(
        SpotifyBuildPlanRequest request,
        Func<SpotifyPlaylistContents, SpotifyPlanBuilder.Result> build,
        CancellationToken token)
    {
        var resolution = await ResolveAsync(request, token);
        if (resolution.Refusal is not null)
            return resolution.AsResult();

        var contents = await _inventory.GetContentsAsync(resolution.Playlist!, token);
        return build(contents);
    }

    private async Task<Resolution> ResolveAsync(SpotifyBuildPlanRequest request, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(request.PlaylistId))
        {
            var byId = await _spotify.GetPlaylistAsync(request.PlaylistId, token);
            return byId is null
                ? new Resolution(null, "I could not find that playlist.")
                : new Resolution(byId, null);
        }

        if (string.IsNullOrWhiteSpace(request.PlaylistReference))
            return new Resolution(null, "Which playlist do you mean?");

        var playlists = await _spotify.GetUserPlaylistsAsync(token);
        var resolved = SpotifyPlaylistResolver.Resolve(request.PlaylistReference, playlists);

        return resolved.Kind switch
        {
            SpotifyPlaylistMatchKind.Resolved => new Resolution(resolved.Playlist, null),
            SpotifyPlaylistMatchKind.Ambiguous => new Resolution(null,
                $"{resolved.Candidates.Count} playlists match “{request.PlaylistReference}”. "
                + "Tell me which one and I will build the plan against it."),
            _ => new Resolution(null, $"I could not find a playlist matching “{request.PlaylistReference}”.")
        };
    }

    private sealed record Resolution(SpotifyPlaylistDto? Playlist, string? Refusal)
    {
        public SpotifyChangePlan? Plan => null;
        public SpotifyPlanBuilder.Result AsResult() => SpotifyPlanBuilder.Result.Refuse(Refusal!);
    }
}
