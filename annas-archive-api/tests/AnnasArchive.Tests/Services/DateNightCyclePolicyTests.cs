using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Services;

public class DateNightCyclePolicyTests
{
    [Fact]
    public void WeeklyDeadlineUtc_KeepsTheWholeHawaiiWeekOpen()
    {
        var deadline = DateNightCycleService.WeeklyDeadlineUtc(new DateOnly(2026, 7, 27));

        // Sunday August 2 at 11:59:59 PM HST is Monday August 3 at 09:59:59 UTC.
        deadline.Should().Be(new DateTime(2026, 8, 3, 9, 59, 59, DateTimeKind.Utc));
    }

    [Fact]
    public void FlyerReminder_IsOwedWhilePersonHasFewerThanThreePrompts()
    {
        var cycle = ActiveCycle(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mom"] = 2
        });

        DateNightCycleService.IsFlyerOwedToday("Mom", cycle).Should().BeTrue();
    }

    [Fact]
    public void FlyerReminder_StopsAfterThreePromptsButCycleRemainsActive()
    {
        var cycle = ActiveCycle(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mom"] = DateNightCycleService.MaxFlyerReminderCount
        });

        DateNightCycleService.IsFlyerOwedToday("Mom", cycle).Should().BeFalse();
        cycle.Status.Should().Be("Active");
    }

    [Fact]
    public void FlyerReminder_CountsAreIndependentForMomAndDad()
    {
        var cycle = ActiveCycle(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mom"] = 3,
            ["Dad"] = 1
        });

        DateNightCycleService.IsFlyerOwedToday("Mom", cycle).Should().BeFalse();
        DateNightCycleService.IsFlyerOwedToday("Dad", cycle).Should().BeTrue();
    }

    private static WeeklyCycle ActiveCycle(Dictionary<string, int> reminderCounts) =>
        new(
            "2026-07-27",
            [1, 2, 3, 4, 5],
            DateTime.UtcNow,
            DateNightCycleService.WeeklyDeadlineUtc(new DateOnly(2026, 7, 27)),
            "Active",
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            new ScheduleState("AwaitingProposal", null, [], null, null, [], null),
            reminderCounts);
}
