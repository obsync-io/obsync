using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// <see cref="ScheduleProfile.GetNextRun"/> returns null for a Cron cadence because Obsync.Shared has
/// no cron engine — so null there means "manual", "never runs", OR "ask the scheduler", and a caller
/// that reads it as "no next run" leaves a cron job showing none at all.
/// <see cref="ScheduleNextRun"/> is the app-side answer, since the app does have Quartz.
/// </summary>
public sealed class ScheduleNextRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cron_GetsARealNextRun_WhereTheSharedModelReturnsNull()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0 3 * * ?" };

        // The gap being closed, stated as an assertion.
        Assert.Null(schedule.GetNextRun(Now));

        var next = ScheduleNextRun.Compute(schedule, Now);

        Assert.NotNull(next);
        Assert.True(next > Now);
        Assert.Equal(3, next!.Value.ToLocalTime().Hour);
    }

    [Fact]
    public void Cron_ReportsTheNextFireItself_NotTheNextOneAWindowWouldAdmit()
    {
        // Deliberate: this matches what the service's reconcile writes, so the app and the service
        // agree rather than alternating. 02:30 is outside the window and is still the answer.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Cron,
            CronExpression = "0 30 2 * * ?",
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(2, 0),
            WindowEnd = new TimeOnly(3, 0),
        };

        var next = ScheduleNextRun.Compute(schedule, Now);

        Assert.NotNull(next);
        Assert.Equal(30, next!.Value.ToLocalTime().Minute);
    }

    [Fact]
    public void Cron_WhoseWindowCanNeverAdmitIt_HasNoNextRun()
    {
        // A zero-length window starves a cron expression as thoroughly as any other cadence, and
        // that much this layer can decide without a cron engine.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Cron,
            CronExpression = "0 0 3 * * ?",
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(3, 0),
            WindowEnd = new TimeOnly(3, 0),
        };

        Assert.Null(ScheduleNextRun.Compute(schedule, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    public void Cron_ThatIsBlankOrUnparseable_HasNoNextRun(string? expression)
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = expression };

        Assert.Null(ScheduleNextRun.Compute(schedule, Now));
    }

    [Fact]
    public void Manual_HasNoNextRun()
    {
        Assert.Null(ScheduleNextRun.Compute(new ScheduleProfile { Kind = ScheduleKind.Manual }, Now));
    }

    [Theory]
    [InlineData(ScheduleKind.Hourly)]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    public void EveryOtherCadence_DefersToTheSharedModel(ScheduleKind kind)
    {
        var schedule = new ScheduleProfile { Kind = kind, IntervalHours = 6, TimeOfDay = new TimeOnly(4, 0) };

        Assert.Equal(schedule.GetNextRun(Now), ScheduleNextRun.Compute(schedule, Now));
    }

    [Fact]
    public void AStarvedNonCronCadence_StillHasNoNextRun()
    {
        // The delegation must not paper over the "never runs" answer the shared model now gives.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDay = new TimeOnly(23, 0),
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(5, 0),
            DayScope = MaintenanceDayScope.WeekdaysOnly,
        };

        Assert.Null(ScheduleNextRun.Compute(schedule, Now));
    }
}
