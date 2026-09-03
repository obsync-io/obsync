using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Obsync.Data.Repositories;
using Obsync.Scheduler;
using Obsync.Shared;
using Obsync.Shared.Models;
using Quartz;
using Quartz.Impl;

namespace Obsync.Integration.Tests;

/// <summary>
/// Reconcile compares the cadence the database asks for against the cadence the live trigger is
/// running, and rebuilds the trigger when they differ. Nothing asserted that the two are comparable
/// in the first place — which is how a custom cron containing a lower-case day name came to be torn
/// down and rebuilt on every 30-second tick, forever: Quartz reports the expression upper-cased, the
/// database keeps the user's original casing, and the raw string comparison was never equal.
/// </summary>
public sealed class SchedulerCronChurnTests : IAsyncLifetime
{
    private const string Group = "obsync";

    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly RecordingLogger _log = new();
    private IScheduler _quartz = null!;
    private SyncJobScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        // A private RAM scheduler, left in standby: reconcile only ever reads and writes the
        // schedule, so nothing needs to fire and no job type needs resolving.
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"churn-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.serializer.type"] = "binary",
        });

        _quartz = await factory.GetScheduler();
        _scheduler = new SyncJobScheduler(factory, _jobs, _log);
    }

    public async Task DisposeAsync() => await _quartz.Shutdown();

    /// <summary>
    /// Counts reschedules by the line <c>ScheduleJobAsync</c> writes on every successful schedule.
    /// The trigger's own start time cannot be used: Quartz truncates it to whole seconds, so a
    /// rebuild and a no-op are indistinguishable within the same second — a test built on it passes
    /// even when the churn is present.
    /// </summary>
    private sealed class RecordingLogger : ILogger<SyncJobScheduler>
    {
        private readonly List<string> _messages = [];

        public int Reschedules => _messages.Count(m => m.StartsWith("Scheduled job", StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Add(formatter(state, exception));
    }

    private static SyncJob CronJob(string cron) => new()
    {
        Name = "Nightly",
        Enabled = true,
        Schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = cron },
    };

    private void Returns(SyncJob job) =>
        _jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<SyncJob> { job });

    private Task<ITrigger?> TriggerAsync(SyncJob job) =>
        _quartz.GetTrigger(new TriggerKey(job.Id.ToString("N"), Group));

    /// <summary>
    /// The regression: after the first tick schedules the job, no later tick may reschedule it.
    /// Before the fix each of these expressions rescheduled on every tick, 2,880 times a day.
    /// </summary>
    [Theory]
    [InlineData("0 0 2 ? * mon-fri")]
    [InlineData("0 0 2 ? * Mon-Fri")]
    [InlineData("0 0 2 ? * mon,wed,fri")]
    [InlineData("0 0 2 ? jan-mar mon")]
    [InlineData("0 0 2 ? * MON-FRI")]
    [InlineData("0 0 2 l * ?")]
    public async Task Reconcile_LeavesAnUnchangedCustomCronAlone_WhateverItsCasing(string cron)
    {
        var job = CronJob(cron);
        Returns(job);

        await _scheduler.ReconcileAsync();
        Assert.Equal(1, _log.Reschedules);

        await _scheduler.ReconcileAsync();
        await _scheduler.ReconcileAsync();

        Assert.Equal(1, _log.Reschedules);
        Assert.NotNull(await TriggerAsync(job));
    }

    /// <summary>
    /// The other half of the invariant: normalizing the comparison must not blind reconcile to a
    /// cadence the user actually edited, which is the whole reason the comparison exists.
    /// </summary>
    [Fact]
    public async Task Reconcile_StillRebuildsTheTrigger_WhenTheCadenceReallyChanged()
    {
        var job = CronJob("0 0 2 ? * mon-fri");
        Returns(job);

        await _scheduler.ReconcileAsync();
        Assert.Equal(1, _log.Reschedules);

        job.Schedule.CronExpression = "0 0 5 ? * mon-fri";
        await _scheduler.ReconcileAsync();

        Assert.Equal(2, _log.Reschedules);
        Assert.Equal("0 0 5 ? * MON-FRI", ((ICronTrigger)(await TriggerAsync(job))!).CronExpressionString);

        // ...and having applied the edit, it settles again rather than churning on the new value.
        await _scheduler.ReconcileAsync();
        Assert.Equal(2, _log.Reschedules);
    }

    /// <summary>
    /// A case-only edit changes the stored text but not the cadence, so the trigger stays as it is.
    /// </summary>
    [Fact]
    public async Task Reconcile_TreatsACaseOnlyEditAsNoChange()
    {
        var job = CronJob("0 0 2 ? * MON-FRI");
        Returns(job);

        await _scheduler.ReconcileAsync();
        job.Schedule.CronExpression = "0 0 2 ? * mon-fri";
        await _scheduler.ReconcileAsync();

        Assert.Equal(1, _log.Reschedules);
    }

    /// <summary>
    /// The machine-generated cadences round-trip byte-identically, so they never churned. Pinning
    /// that keeps the normalized comparison honest for them too.
    /// </summary>
    [Theory]
    [InlineData(ScheduleKind.Hourly)]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    public async Task Reconcile_LeavesTheBuiltInCadencesAlone(ScheduleKind kind)
    {
        var job = new SyncJob
        {
            Name = "Nightly",
            Enabled = true,
            Schedule = new ScheduleProfile
            {
                Kind = kind,
                IntervalHours = 6,
                TimeOfDay = new TimeOnly(2, 30),
                DayOfWeek = DayOfWeek.Wednesday,
            },
        };
        Returns(job);

        await _scheduler.ReconcileAsync();
        await _scheduler.ReconcileAsync();

        Assert.Equal(1, _log.Reschedules);
    }

    /// <summary>A job with no trigger at all is still scheduled — null must not read as "matches".</summary>
    [Fact]
    public async Task Reconcile_SchedulesAJobWhoseTriggerIsMissing()
    {
        var job = CronJob("0 0 2 ? * mon-fri");
        Returns(job);

        await _scheduler.ReconcileAsync();
        await _quartz.UnscheduleJob(new TriggerKey(job.Id.ToString("N"), Group));
        Assert.Null(await TriggerAsync(job));

        await _scheduler.ReconcileAsync();

        Assert.NotNull(await TriggerAsync(job));
        Assert.Equal(2, _log.Reschedules);
    }
}
