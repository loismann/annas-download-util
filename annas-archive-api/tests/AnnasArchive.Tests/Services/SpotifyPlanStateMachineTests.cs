using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;

namespace AnnasArchive.Tests.Services;

public class SpotifyPlanStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_AssignsSafetyTierAndThirtyMinuteExpiry()
    {
        var plan = SpotifyPlanStateMachine.Create(
            SpotifyPlanAction.MergePlaylists,
            [new SpotifyPlanTarget("playlist-1", "Road Trip", "snapshot-1")],
            Now);

        plan.Status.Should().Be(SpotifyPlanStatus.Draft);
        plan.SafetyTier.Should().Be(SpotifyPlanSafetyTier.HighImpact);
        plan.ExpiresAtUtc.Should().Be(Now.AddMinutes(30));
        plan.Targets.Should().ContainSingle();
    }

    [Fact]
    public void HappyPath_RequiresReviewThenConfirmationBeforeCompletion()
    {
        var draft = SpotifyPlanStateMachine.Create(SpotifyPlanAction.RenamePlaylist, [], Now);
        var awaiting = SpotifyPlanStateMachine.MarkAwaitingConfirmation(draft, Now.AddMinutes(1));
        var executing = SpotifyPlanStateMachine.Confirm(awaiting, "Paul", Now.AddMinutes(2));
        var completed = SpotifyPlanStateMachine.Complete(executing);

        awaiting.Status.Should().Be(SpotifyPlanStatus.AwaitingConfirmation);
        executing.Status.Should().Be(SpotifyPlanStatus.Executing);
        executing.ConfirmedBy.Should().Be("Paul");
        executing.ConfirmedAtUtc.Should().Be(Now.AddMinutes(2));
        completed.Status.Should().Be(SpotifyPlanStatus.Completed);
    }

    [Fact]
    public void Confirm_IsIdempotentForSameIdentity()
    {
        var awaiting = SpotifyPlanStateMachine.MarkAwaitingConfirmation(
            SpotifyPlanStateMachine.Create(SpotifyPlanAction.AddItems, [], Now),
            Now);
        var executing = SpotifyPlanStateMachine.Confirm(awaiting, "Paul", Now.AddMinutes(1));

        var duplicate = SpotifyPlanStateMachine.Confirm(executing, "Paul", Now.AddMinutes(2));

        duplicate.Should().Be(executing);
        duplicate.ConfirmedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Confirm_RejectsExpiredPlan()
    {
        var awaiting = SpotifyPlanStateMachine.MarkAwaitingConfirmation(
            SpotifyPlanStateMachine.Create(
                SpotifyPlanAction.AddItems,
                [],
                Now,
                TimeSpan.FromMinutes(5)),
            Now);

        var act = () => SpotifyPlanStateMachine.Confirm(awaiting, "Paul", Now.AddMinutes(5));

        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }

    [Fact]
    public void Complete_RejectsUnconfirmedDraft()
    {
        var draft = SpotifyPlanStateMachine.Create(SpotifyPlanAction.CreatePlaylist, [], Now);

        var act = () => SpotifyPlanStateMachine.Complete(draft);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Draft*Executing*");
    }

    [Theory]
    [InlineData(SpotifyPlanAction.CreatePlaylist, SpotifyPlanSafetyTier.Additive)]
    [InlineData(SpotifyPlanAction.RemoveItems, SpotifyPlanSafetyTier.Modifying)]
    [InlineData(SpotifyPlanAction.ReplaceItems, SpotifyPlanSafetyTier.HighImpact)]
    [InlineData(SpotifyPlanAction.RemovePlaylistsFromLibrary, SpotifyPlanSafetyTier.HighImpact)]
    public void SafetyTierFor_MapsMutationRisk(
        SpotifyPlanAction action,
        SpotifyPlanSafetyTier expected)
    {
        SpotifyPlanStateMachine.SafetyTierFor(action).Should().Be(expected);
    }
}
