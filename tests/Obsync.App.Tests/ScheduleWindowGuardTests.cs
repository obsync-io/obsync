using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// <see cref="ScheduleWindowGuard"/> adds the one starvation case the shared model cannot answer:
/// a Cron expression, whose fire times need a cron engine. What matters here is the probe's edges —
/// it must look far enough ahead not to condemn a yearly expression, and it must stay silent rather
/// than guess when it runs out of budget before covering the horizon.
/// </summary>
public sealed class ScheduleWindowGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ScheduleProfile Cron(string expression, TimeOnly start, TimeOnly end,
        MaintenanceDayScope scope = MaintenanceDayScope.AnyDay) => new()
        {
            Kind = ScheduleKind.Cron,
            CronExpression = expression,
            MaintenanceWindowEnabled = true,
            WindowStart = start,
            WindowEnd = end,
            DayScope = scope,
        };

    [Fact]
    public void Cron_FiringOutsideTheWindowEveryDay_IsRefused()
    {
        var reason = ScheduleWindowGuard.ConflictReason(
            Cron("0 30 2 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0)), Now);

        Assert.NotNull(reason);
        Assert.Contains("0 30 2 * * ?", reason);
        Assert.Contains("fires only outside the maintenance window", reason);
    }

    [Fact]
    public void Cron_WhoseOnlyInWindowFireIsADaylightSavingShift_IsStillRefused()
    {
        // The subtle one. In a zone that springs forward, 02:30 does not exist on the transition
        // day, so Quartz nudges that single fire to 03:30 — inside a 03:00-05:00 window. One
        // accidental run a year is not a working schedule, and counting it would have made this
        // whole guard useless in every DST zone. The zone is pinned so the case is exercised
        // wherever the suite runs, not only on a machine that happens to observe DST.
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        var reason = ScheduleWindowGuard.ConflictReason(
            Cron("0 30 2 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0)), Now, pacific);

        Assert.NotNull(reason);
        Assert.Contains("fires only outside", reason);
    }

    [Fact]
    public void Cron_FiringInsideTheWindowEveryDay_IsAllowedInADaylightSavingZone()
    {
        // The companion check: pinning the zone must not make a genuinely fine schedule fail.
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        Assert.Null(ScheduleWindowGuard.ConflictReason(
            Cron("0 30 3 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0)), Now, pacific));
    }

    [Fact]
    public void Cron_FiringInsideTheWindow_IsAllowed()
    {
        Assert.Null(ScheduleWindowGuard.ConflictReason(
            Cron("0 30 3 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0)), Now));
    }

    [Fact]
    public void Cron_DayScopeTheExpressionCanNeverMeet_IsRefused()
    {
        // Weekdays at noon against a weekends-only overnight window: doubly starved.
        var reason = ScheduleWindowGuard.ConflictReason(
            Cron("0 0 12 ? * MON-FRI", new TimeOnly(22, 0), new TimeOnly(5, 0), MaintenanceDayScope.WeekendsOnly),
            Now);

        Assert.NotNull(reason);
        Assert.Contains("weekends", reason);
    }

    [Fact]
    public void Cron_YearlyExpressionInsideTheWindow_IsAllowed()
    {
        // The reason the horizon is 400 days and not a week: this fires once a year, and a shorter
        // probe would condemn a schedule that runs perfectly well.
        Assert.Null(ScheduleWindowGuard.ConflictReason(
            Cron("0 0 3 1 1 ?", new TimeOnly(2, 0), new TimeOnly(4, 0)), Now));
    }

    [Fact]
    public void Cron_YearlyExpressionOutsideTheWindow_IsRefused()
    {
        Assert.NotNull(ScheduleWindowGuard.ConflictReason(
            Cron("0 0 3 1 1 ?", new TimeOnly(5, 0), new TimeOnly(6, 0)), Now));
    }

    [Fact]
    public void Cron_ThatStopsFiringAltogether_IsJudgedOnTheFiresItHas()
    {
        // Year-bounded: one fire ever, at 02:00, which the window never admits. The probe runs out
        // of fires rather than out of horizon, and that still counts as a complete scan.
        Assert.NotNull(ScheduleWindowGuard.ConflictReason(
            Cron("0 0 2 1 1 ? 2027", new TimeOnly(3, 0), new TimeOnly(5, 0)), Now));
    }

    [Fact]
    public void Cron_TooDenseToScanTheWholeHorizon_IsAllowedRatherThanGuessedAt()
    {
        // Every minute of hours 00 and 01 is 120 fires a day, so the probe budget runs out around
        // day 166 — well short of the horizon. This one genuinely never runs, but refusing a
        // schedule on an unfinished scan is the worse error, so the guard stays silent.
        Assert.Null(ScheduleWindowGuard.ConflictReason(
            Cron("0 * 0-1 * * ?", new TimeOnly(2, 15), new TimeOnly(2, 45)), Now));
    }

    [Fact]
    public void Cron_WithNoWindow_IsAllowed()
    {
        var schedule = Cron("0 30 2 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0));
        schedule.MaintenanceWindowEnabled = false;

        Assert.Null(ScheduleWindowGuard.ConflictReason(schedule, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    public void Cron_ThatIsBlankOrUnparseable_IsLeftToTheSyntaxRules(string? expression)
    {
        // Those have their own messages; saying "never fires inside the window" about an expression
        // that does not parse would only obscure the real problem.
        var schedule = Cron("0 30 2 * * ?", new TimeOnly(3, 0), new TimeOnly(5, 0));
        schedule.CronExpression = expression;

        Assert.Null(ScheduleWindowGuard.ConflictReason(schedule, Now));
    }

    [Fact]
    public void NonCronCadences_AreDelegatedToTheSharedRule()
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = 7,
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(1, 0),
            WindowEnd = new TimeOnly(4, 0),
        };

        Assert.Equal(schedule.MaintenanceWindowConflictReason(),
            ScheduleWindowGuard.ConflictReason(schedule, Now));
        Assert.NotNull(ScheduleWindowGuard.ConflictReason(schedule, Now));
    }
}
