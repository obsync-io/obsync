using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// An Hourly job's trigger is <c>0 0 0/N * * ?</c>, whose step restarts at hour 0 every midnight —
/// so its occurrences are the hours where <c>hour % N == 0</c>, and the last gap of the day is short
/// whenever N does not divide 24 (N=5 runs 20:00 then 00:00, four hours later).
///
/// <see cref="ScheduleProfile.GetNextRun"/>'s window-advance loop used to step by N hours, which
/// leaves that grid the moment it crosses midnight and then stops on whatever off-grid hour the
/// window happens to admit — reporting a "next run" the job can never fire at. These pin the
/// returned value to the grid, not merely to the window, which is what the previous test could not
/// distinguish.
/// </summary>
public sealed class ScheduleWindowNextRunGridTests
{
    // Local-wall-clock anchor: GetNextRun works in local time, so grid tests pin the local date.
    private static DateTimeOffset LocalAt(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private static ScheduleProfile Hourly(int interval, TimeOnly start, TimeOnly end,
        MaintenanceDayScope scope = MaintenanceDayScope.AnyDay) => new()
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = interval,
            MaintenanceWindowEnabled = true,
            WindowStart = start,
            WindowEnd = end,
            DayScope = scope,
        };

    [Fact]
    public void Hourly_EveryFiveHours_ReturnsTheRealFireTimeNotADriftedOne()
    {
        // The reported case. Grid is 00/05/10/15/20 and the window admits only 05:00 (00:00 falls
        // before 00:30). From 12:00 the advance walks 15:00 → 20:00 → and the next occurrence is
        // 00:00, not 01:00. Stepping by 5 produced 01:00, which the window accepts and which is not
        // a fire time at all; the job would really have run at 05:00, five hours later.
        var schedule = Hourly(5, new TimeOnly(0, 30), new TimeOnly(6, 0));

        var next = schedule.GetNextRun(LocalAt(2026, 3, 1, 12));

        Assert.NotNull(next);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 3, 2, 5, 0, 0), local.DateTime);
        Assert.Equal(0, local.Hour % 5); // on the grid
        Assert.True(schedule.IsWithinMaintenanceWindow(next.Value));
    }

    [Fact]
    public void Hourly_EverySixteenHours_ReturnsTheNextOccurrenceNotTheOneADayLater()
    {
        // The second failure mode: stepping by 16 from 16:00 lands on 08:00 (off-grid, and the
        // window rejects it) and then back on 00:00 — but a whole day late. The grid is only
        // 00:00 and 16:00, since 32 exceeds the hour field.
        var schedule = Hourly(16, new TimeOnly(22, 0), new TimeOnly(5, 0));

        var next = schedule.GetNextRun(LocalAt(2026, 3, 1, 0, 17));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 2, 0, 0, 0), next!.Value.ToLocalTime().DateTime);
    }

    [Fact]
    public void Hourly_EverySevenHours_DoesNotReportATimeEarlierThanTheRealFire()
    {
        // Drift is not one-directional. Grid 00/07/14/21; the window admits 07:00. From 20:30 the
        // first candidate is 21:00, and stepping by 7 gave 04:00 the next day — inside the window,
        // so it was returned, three hours EARLIER than the real 07:00 fire. A next-run that passes
        // by more than five minutes is exactly what IsScheduleOverdue reads, so this direction
        // produced a false "Overdue" badge blaming a service that was working.
        var schedule = Hourly(7, new TimeOnly(3, 0), new TimeOnly(8, 0));

        var next = schedule.GetNextRun(LocalAt(2026, 3, 1, 20, 30));

        Assert.NotNull(next);
        var local = next!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 3, 2, 7, 0, 0), local.DateTime);
        Assert.Equal(0, local.Hour % 7);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(23)]
    public void Hourly_WithAWindow_AlwaysLandsOnTheCronGrid(int interval)
    {
        // Sweeps every hour of a day for the intervals that do not divide 24 — the ones that drift.
        // Both windows are mostly closed, so the advance loop is genuinely exercised; which of them
        // exposes a given interval varies, so a single window is not enough to cover them all.
        var windows = new[]
        {
            (Start: new TimeOnly(22, 0), End: new TimeOnly(5, 0)),
            (Start: new TimeOnly(0, 30), End: new TimeOnly(6, 0)),
        };

        foreach (var (start, end) in windows)
        {
            var schedule = Hourly(interval, start, end);

            // Starved combinations are refused at save time, so GetNextRun is never asked about them.
            if (schedule.MaintenanceWindowConflictReason() is not null)
            {
                continue;
            }

            for (var hour = 0; hour < 24; hour++)
            {
                var from = LocalAt(2026, 3, 1, hour, 17);

                var next = schedule.GetNextRun(from);

                Assert.NotNull(next);
                var local = next!.Value.ToLocalTime();
                Assert.True(next > from, $"interval {interval} from {hour:00}:17 went backwards");
                Assert.Equal(0, local.Minute);
                Assert.Equal(0, local.Hour % interval);
                Assert.True(schedule.IsWithinMaintenanceWindow(next.Value),
                    $"interval {interval} from {hour:00}:17 returned {local} outside {start}-{end}");
            }
        }
    }

    [Fact]
    public void Hourly_WithoutAWindow_IsUnchanged()
    {
        // The first candidate was always correct; only the advance loop was wrong. This pins that
        // the fix did not disturb the window-free path.
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = 5 };

        var next = schedule.GetNextRun(LocalAt(2026, 3, 1, 12));

        Assert.Equal(new DateTime(2026, 3, 1, 15, 0, 0), next!.Value.ToLocalTime().DateTime);
    }

    [Fact]
    public void Hourly_DivisorOfTwentyFour_KeepsTheValueItAlreadyReturned()
    {
        // N=6 divides 24, so stepping by N never left the grid and this value is unchanged by the
        // fix — the guarantee that the correction is confined to the intervals that actually drift.
        var schedule = Hourly(6, new TimeOnly(22, 0), new TimeOnly(5, 0));

        var next = schedule.GetNextRun(LocalAt(2026, 3, 1, 12));

        Assert.Equal(new DateTime(2026, 3, 2, 0, 0, 0), next!.Value.ToLocalTime().DateTime);
    }
}
