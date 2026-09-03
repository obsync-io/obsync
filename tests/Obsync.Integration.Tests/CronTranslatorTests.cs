using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Integration.Tests;

/// <summary>
/// Nothing previously asserted that a <see cref="ScheduleProfile"/> a user can build translates to
/// a cron expression Quartz will accept — which is how an hourly interval of 24+ shipped: it
/// produced <c>0 0 0/24 * * ?</c>, a step in the hour field (valid range 0-23), so Quartz rejected
/// it and the job was left enabled with no trigger.
/// </summary>
public sealed class CronTranslatorTests
{
    /// <summary>
    /// The load-bearing invariant: the schedulability rule the app and importer enforce must agree
    /// with what Quartz actually accepts. If these two ever drift apart, a job can be saved that
    /// can never run.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(23)]
    public void Hourly_EverySchedulableInterval_ProducesACronQuartzAccepts(int interval)
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = interval };
        Assert.Null(schedule.UnschedulableReason());

        var cron = CronTranslator.ToCron(schedule);

        Assert.NotNull(cron);
        Assert.True(CronExpression.IsValidExpression(cron), $"Quartz rejected '{cron}'");
        Assert.True(CronTranslator.IsValid(schedule));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(48)]
    public void Hourly_BeyondTwentyThree_IsRejectedByBothTheRuleAndQuartz(int interval)
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly, IntervalHours = interval };

        // The rule refuses it, so it can no longer be saved...
        Assert.NotNull(schedule.UnschedulableReason());
        // ...and the reason the rule exists: Quartz would refuse the translation anyway.
        Assert.False(CronExpression.IsValidExpression(CronTranslator.ToCron(schedule)!));
        Assert.False(CronTranslator.IsValid(schedule));
    }

    [Fact]
    public void EverySchedulableCadence_TranslatesToACronQuartzAccepts()
    {
        // A sweep rather than spot checks, so a future cadence or field cannot quietly reintroduce
        // an untranslatable combination.
        foreach (var schedule in EnumerateSchedules())
        {
            if (schedule.UnschedulableReason() is not null)
            {
                continue;
            }

            var cron = CronTranslator.ToCron(schedule);
            if (schedule.Kind == ScheduleKind.Manual)
            {
                Assert.Null(cron);
                continue;
            }

            Assert.True(
                CronExpression.IsValidExpression(cron!),
                $"{schedule.Kind} interval={schedule.IntervalHours} time={schedule.TimeOfDay} " +
                $"day={schedule.DayOfWeek} produced '{cron}', which Quartz rejects");
        }
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    public void Weekly_FiresOnTheRequestedDay(DayOfWeek day)
    {
        var schedule = new ScheduleProfile
        {
            Kind = ScheduleKind.Weekly,
            DayOfWeek = day,
            TimeOfDay = new TimeOnly(3, 0),
        };

        var next = CronTranslator.NextFire(CronTranslator.ToCron(schedule)!, DateTimeOffset.UtcNow);

        Assert.NotNull(next);
        Assert.Equal(day, next.Value.ToLocalTime().DayOfWeek);
    }

    [Fact]
    public void Manual_TranslatesToNothing()
    {
        var schedule = new ScheduleProfile { Kind = ScheduleKind.Manual };

        Assert.Null(CronTranslator.ToCron(schedule));
        Assert.False(CronTranslator.IsValid(schedule));
    }

    /// <summary>
    /// The comparison reconcile makes every 30 seconds. It has to hold for the expression as Quartz
    /// reports it back, not as the user typed it, or the trigger is rebuilt forever.
    /// </summary>
    [Theory]
    [InlineData("0 0 2 ? * mon-fri")]
    [InlineData("0 0 2 ? * Mon-Fri")]
    [InlineData("0 0 2 ? * MON-FRI")]
    [InlineData("0 0 2 ? * mon,wed,fri")]
    [InlineData("0 0 2 ? jan-mar mon")]
    [InlineData("0 0 2 ? * sun#2")]
    [InlineData("0 0 2 L * ?")]
    [InlineData("0 0 23 * * ?")]
    [InlineData("  0 0 2 ? * mon-fri  ")]
    public void MatchesTrigger_HoldsForTheExpressionQuartzReportsBack(string cron)
    {
        // Built exactly as SyncJobScheduler builds it, so the round-trip under test is the real one.
        var trigger = (ICronTrigger)TriggerBuilder.Create()
            .WithIdentity("t", "obsync")
            .WithCronSchedule(cron, x => x.InTimeZone(TimeZoneInfo.Local))
            .Build();

        Assert.True(
            CronTranslator.MatchesTrigger(trigger.CronExpressionString, cron),
            $"stored '{cron}' did not match the trigger's '{trigger.CronExpressionString}'");
    }

    [Fact]
    public void MatchesTrigger_IsFalseForADifferentCadence()
    {
        var trigger = (ICronTrigger)TriggerBuilder.Create()
            .WithIdentity("t", "obsync")
            .WithCronSchedule("0 0 2 ? * MON-FRI", x => x.InTimeZone(TimeZoneInfo.Local))
            .Build();

        Assert.False(CronTranslator.MatchesTrigger(trigger.CronExpressionString, "0 0 5 ? * mon-fri"));
        Assert.False(CronTranslator.MatchesTrigger(trigger.CronExpressionString, "0 0 2 ? * mon-thu"));
    }

    /// <summary>A job with no trigger must reschedule, so a missing expression can never match.</summary>
    [Fact]
    public void MatchesTrigger_IsFalseWhenThereIsNoTrigger() =>
        Assert.False(CronTranslator.MatchesTrigger(null, "0 0 2 ? * mon-fri"));

    /// <summary>
    /// The built-in cadences are machine-generated and already in Quartz's normal form, so the
    /// comparison must hold for every one of them as well.
    /// </summary>
    [Fact]
    public void MatchesTrigger_HoldsForEveryBuiltInCadence()
    {
        foreach (var schedule in EnumerateSchedules())
        {
            var cron = CronTranslator.ToCron(schedule);
            if (string.IsNullOrWhiteSpace(cron) || !CronExpression.IsValidExpression(cron))
            {
                continue;
            }

            var trigger = (ICronTrigger)TriggerBuilder.Create()
                .WithIdentity("t", "obsync")
                .WithCronSchedule(cron, x => x.InTimeZone(TimeZoneInfo.Local))
                .Build();

            Assert.True(
                CronTranslator.MatchesTrigger(trigger.CronExpressionString, cron),
                $"{schedule.Kind} produced '{cron}' but the trigger reported '{trigger.CronExpressionString}'");
        }
    }

    private static IEnumerable<ScheduleProfile> EnumerateSchedules()
    {
        foreach (var kind in Enum.GetValues<ScheduleKind>())
        {
            if (kind == ScheduleKind.Cron)
            {
                continue; // user-supplied text, validated separately by the wizard
            }

            foreach (var interval in new[] { -1, 0, 1, 2, 7, 23, 24, 99 })
            {
                foreach (var time in new[] { new TimeOnly(0, 0), new TimeOnly(13, 37), new TimeOnly(23, 59) })
                {
                    foreach (var day in Enum.GetValues<DayOfWeek>())
                    {
                        yield return new ScheduleProfile
                        {
                            Kind = kind,
                            IntervalHours = interval,
                            TimeOfDay = time,
                            DayOfWeek = day,
                        };
                    }
                }
            }
        }
    }
}
