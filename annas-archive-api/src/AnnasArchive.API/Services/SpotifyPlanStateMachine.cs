using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

/// <summary>
/// Defines the legal lifecycle for a Spotify change plan. This class performs no
/// Spotify writes; later plan persistence/execution must go through these
/// transitions instead of assigning statuses directly.
/// </summary>
public static class SpotifyPlanStateMachine
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    public static SpotifyChangePlan Create(
        SpotifyPlanAction action,
        IReadOnlyList<SpotifyPlanTarget>? targets,
        DateTimeOffset nowUtc,
        TimeSpan? lifetime = null)
    {
        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Plan lifetime must be positive.");

        return new SpotifyChangePlan(
            Guid.NewGuid(),
            action,
            SafetyTierFor(action),
            SpotifyPlanStatus.Draft,
            nowUtc,
            nowUtc.Add(effectiveLifetime),
            targets?.ToArray() ?? []);
    }

    public static SpotifyChangePlan MarkAwaitingConfirmation(
        SpotifyChangePlan plan,
        DateTimeOffset nowUtc)
    {
        if (plan.Status == SpotifyPlanStatus.AwaitingConfirmation)
            return plan;

        EnsureNotExpired(plan, nowUtc);
        EnsureStatus(plan, SpotifyPlanStatus.Draft);
        return plan with { Status = SpotifyPlanStatus.AwaitingConfirmation };
    }

    public static SpotifyChangePlan Confirm(
        SpotifyChangePlan plan,
        string confirmedBy,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(confirmedBy))
            throw new ArgumentException("A confirming identity is required.", nameof(confirmedBy));

        if (plan.Status == SpotifyPlanStatus.Executing &&
            string.Equals(plan.ConfirmedBy, confirmedBy, StringComparison.Ordinal))
        {
            return plan;
        }

        EnsureNotExpired(plan, nowUtc);
        EnsureStatus(plan, SpotifyPlanStatus.AwaitingConfirmation);

        return plan with
        {
            Status = SpotifyPlanStatus.Executing,
            ConfirmedBy = confirmedBy,
            ConfirmedAtUtc = nowUtc
        };
    }

    public static SpotifyChangePlan Complete(SpotifyChangePlan plan) =>
        MarkOutcome(plan, SpotifyPlanStatus.Completed);

    public static SpotifyChangePlan CompletePartially(SpotifyChangePlan plan, string failure) =>
        MarkOutcome(plan, SpotifyPlanStatus.PartiallyCompleted, failure);

    public static SpotifyChangePlan Fail(SpotifyChangePlan plan, string failure) =>
        MarkOutcome(plan, SpotifyPlanStatus.Failed, failure);

    public static SpotifyChangePlan Cancel(SpotifyChangePlan plan)
    {
        if (plan.Status == SpotifyPlanStatus.Cancelled)
            return plan;

        if (plan.Status is not (SpotifyPlanStatus.Draft or SpotifyPlanStatus.AwaitingConfirmation))
            throw InvalidTransition(plan.Status, SpotifyPlanStatus.Cancelled);

        return plan with { Status = SpotifyPlanStatus.Cancelled };
    }

    public static SpotifyChangePlan Expire(SpotifyChangePlan plan, DateTimeOffset nowUtc)
    {
        if (plan.Status == SpotifyPlanStatus.Expired)
            return plan;

        if (!plan.IsExpired(nowUtc))
            throw new InvalidOperationException("A plan cannot expire before its expiry time.");

        if (plan.Status is not (SpotifyPlanStatus.Draft or SpotifyPlanStatus.AwaitingConfirmation))
            throw InvalidTransition(plan.Status, SpotifyPlanStatus.Expired);

        return plan with { Status = SpotifyPlanStatus.Expired };
    }

    public static SpotifyChangePlan Revert(SpotifyChangePlan plan)
    {
        if (plan.Status == SpotifyPlanStatus.Reverted)
            return plan;

        if (plan.Status is not (SpotifyPlanStatus.Completed or SpotifyPlanStatus.PartiallyCompleted))
            throw InvalidTransition(plan.Status, SpotifyPlanStatus.Reverted);

        return plan with { Status = SpotifyPlanStatus.Reverted };
    }

    public static SpotifyPlanSafetyTier SafetyTierFor(SpotifyPlanAction action) => action switch
    {
        SpotifyPlanAction.CreatePlaylist or SpotifyPlanAction.AddItems =>
            SpotifyPlanSafetyTier.Additive,

        SpotifyPlanAction.RenamePlaylist or
        SpotifyPlanAction.ChangePlaylistDetails or
        SpotifyPlanAction.RemoveItems or
        SpotifyPlanAction.ReorderItems or
        SpotifyPlanAction.RestorePreviousChange =>
            SpotifyPlanSafetyTier.Modifying,

        SpotifyPlanAction.ReplaceItems or
        SpotifyPlanAction.MergePlaylists or
        SpotifyPlanAction.RemovePlaylistsFromLibrary =>
            SpotifyPlanSafetyTier.HighImpact,

        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown Spotify plan action.")
    };

    private static SpotifyChangePlan MarkOutcome(
        SpotifyChangePlan plan,
        SpotifyPlanStatus outcome,
        string? failure = null)
    {
        if ((outcome is SpotifyPlanStatus.Failed or SpotifyPlanStatus.PartiallyCompleted) &&
            string.IsNullOrWhiteSpace(failure))
        {
            throw new ArgumentException("Failed and partial outcomes require an explanation.", nameof(failure));
        }

        if (plan.Status == outcome)
            return plan;

        EnsureStatus(plan, SpotifyPlanStatus.Executing);
        return plan with { Status = outcome, Failure = failure };
    }

    private static void EnsureNotExpired(SpotifyChangePlan plan, DateTimeOffset nowUtc)
    {
        if (plan.IsExpired(nowUtc))
            throw new InvalidOperationException("The Spotify change plan has expired and must be rebuilt.");
    }

    private static void EnsureStatus(SpotifyChangePlan plan, SpotifyPlanStatus expected)
    {
        if (plan.Status != expected)
            throw InvalidTransition(plan.Status, expected);
    }

    private static InvalidOperationException InvalidTransition(
        SpotifyPlanStatus current,
        SpotifyPlanStatus requested) =>
        new($"Spotify plan cannot transition from {current} to {requested}.");
}
