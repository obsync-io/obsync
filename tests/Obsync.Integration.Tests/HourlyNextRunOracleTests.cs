using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Integration.Tests;

/// <summary>
/// <see cref="ScheduleProfile.GetNextRun"/> and the Quartz trigger the scheduler actually builds are
/// two independent answers to "when does this run next", and nothing compared them. That is how the
/// hourly window-advance came to step by N hours while the real trigger, <c>0 0 0/N * * ?</c>,
/// restarts its step at midnight — so for every N that does not divide 24 the preview drifted off
/// the grid and reported a time the job could never fire at.
///
/// These tests use the real cron engine as the oracle rather than a hand-written expectation, so
/// they keep holding if either side changes. <see cref="ScheduleProfile"/> deliberately has no cron
/// dependency, which is why the comparison lives here rather than beside the model's own tests.
/// </summary>
public sealed class HourlyNextRunOracleTests
{
    /// <summary>The next occurrence at or after <paramref name="afterUtc"/> that the window admits.</summary>
    private static DateTimeOffset? TrueNextInWindow(ScheduleProfile schedule, DateTimeOffset afterUtc)
    {
        var cron = CronTranslator.ToCron(schedule)!;
        var expression = new CronExpression(cron) { TimeZone = TimeZoneInfo.Local };
        var at = afterUtc;
        for (var probe = 0; probe < 2000; probe++)
        {
            if (expression.GetNextValidTimeAfter(at) is not { } fire)
            {
                return null;
            }

            if (schedule.IsWithinMaintenanceWindow(fire.ToLocalTime()))
            {
                return fire;
            }

            at = fire;
        }

        return null;
    }

    public static TheoryData<int> Intervals()
    {
        var data = new TheoryData<int>();
        for (var interval = 1; interval <= ScheduleProfile.MaxHourlyInterval; interval++)
        {
            data.Add(interval);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Intervals))]
    public void GetNextRun_MatchesQuartz_ForEverySchedulableIntervalAndWindow(int interval)
    {
        // Several window shapes: one that excludes midnight (the reported case), an overnight wrap,
        // a business-hours block, and a half-day. Each is checked from every hour of the day, since
        // the drift only appeared once the advance loop crossed midnight.
        var windows = new[]
        {
            (Start: new TimeOnly(0, 30), End: new TimeOnly(6, 0)),
            (Start: new TimeOnly(22, 0), End: new TimeOnly(5, 0)),
            (Start: new TimeOnly(9, 0), End: new TimeOnly(17, 0)),
            (Start: new TimeOnly(0, 0), End: new TimeOnly(12, 0)),
        };

        foreach (var (start, end) in windows)
        {
            var schedule = new ScheduleProfile
            {
                Kind = ScheduleKind.Hourly,
                IntervalHours = interval,
                MaintenanceWindowEnabled = true,
                WindowStart = start,
                WindowEnd = end,
            };

            // Starved windows are included deliberately. The oracle runs out of occurrences and
            // returns null for them, and GetNextRun now says null too instead of handing back the
            // last out-of-window candidate — so the two agree on "never", not just on "when".
            var starved = schedule.MaintenanceWindowConflictReason() is not null;

            for (var hour = 0; hour < 24; hour++)
            {
                var from = new DateTimeOffset(new DateTime(2026, 3, 1, hour, 17, 0, DateTimeKind.Local));

                var predicted = schedule.GetNextRun(from);
                var actual = TrueNextInWindow(schedule, from);

                Assert.Equal(starved, predicted is null);
                Assert.Equal(actual, predicted);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Intervals))]
    public void GetNextRun_WithoutAWindow_MatchesQuartz(int interval)
    {
        // The seed candidate was never the broken part; this keeps it that way.
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = interval };

        for (var hour = 0; hour < 24; hour++)
        {
            var from = new DateTimeOffset(new DateTime(2026, 3, 1, hour, 17, 0, DateTimeKind.Local));

            Assert.Equal(CronTranslator.NextRun(schedule, from), schedule.GetNextRun(from));
        }
    }

    [Fact]
    public void GetNextRun_ReportedCase_EveryFiveHoursWithAnEarlyMorningWindow()
    {
        // Named explicitly so the regression is recognisable: the finding reported a preview of
        // 01:00 against a real fire of 05:00, and 01:00 is not an occurrence of this trigger at all.
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Hourly,
            IntervalHours = 5,
            MaintenanceWindowEnabled = true,
            WindowStart = new TimeOnly(0, 30),
            WindowEnd = new TimeOnly(6, 0),
        };
        var from = new DateTimeOffset(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local));

        var predicted = schedule.GetNextRun(from);

        Assert.Equal(TrueNextInWindow(schedule, from), predicted);
        Assert.Equal(new DateTime(2026, 3, 2, 5, 0, 0), predicted!.Value.ToLocalTime().DateTime);

        // The sharper assertion: whatever it returns must be a real occurrence of the job's trigger.
        var expression = new CronExpression(CronTranslator.ToCron(schedule)!) { TimeZone = TimeZoneInfo.Local };
        Assert.True(expression.IsSatisfiedBy(predicted.Value), $"{predicted} is not a fire time of this trigger");
    }
}
