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
            _ => SpotifyPlanBuilder.Result.Refuse(
                $"{request.Action} is not available yet — it arrives with the bulk cleanup phase.")
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
        plan.CanUndo, plan.UndoOfPlanId);

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
