using Microsoft.Extensions.Logging;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Quartz;

namespace Obsync.Scheduler;

/// <summary>
/// Writes down a scheduled occurrence that Quartz discarded, so a dropped run can never again be
/// invisible.
/// </summary>
/// <remarks>
/// Cron triggers are built with <c>WithMisfireHandlingInstructionDoNothing</c> (see
/// <see cref="SyncJobScheduler"/>), which is the right policy — it is what stops a run that outlasts
/// its own interval from being restarted the instant it finishes, forever. What it does NOT do is
/// tell anyone. Quartz applies the instruction to any trigger it acquires later than its misfire
/// threshold, that threshold defaults to <b>five seconds</b>, and the service caps Quartz at two
/// concurrent jobs — so an occurrence merely QUEUED behind two other jobs is silently dropped and
/// the trigger advances to its next slot.
/// <para>
/// Measured against the shipped configuration: five jobs sharing one cron time, two workers, runs
/// longer than five seconds — jobs three, four and five fired <b>zero</b> times across repeated
/// cycles while both workers sat idle most of every period, with no log line at any level, not even
/// at Debug. The loss is also invisible to every health surface, because the misfired trigger holds
/// a confident FUTURE next-fire time: reconcile copies that into <c>NextRunAt</c> within 30 seconds,
/// so <see cref="SyncJob.IsScheduleOverdue"/> can never trip and the heartbeat stays green.
/// </para>
/// <para>
/// This listener does not change the policy — raising the threshold or switching to
/// <c>FireAndProceed</c> reintroduces the permanent-backlog loop that <c>DoNothing</c> exists to
/// prevent. It makes the skip observable instead: a Warning in the log, and a
/// <see cref="RunStatus.Skipped"/> row in History, which is the same signal the engine already
/// writes when a run is dropped for lock contention. An operator seeing repeated skips for the same
/// job should stagger its cron time or raise the worker count.
/// </para>
/// </remarks>
public sealed class MisfiredOccurrenceListener : ITriggerListener
{
    private readonly IJobRepository _jobs;
    private readonly IRunRepository _runs;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<MisfiredOccurrenceListener> _logger;

    public MisfiredOccurrenceListener(
        IJobRepository jobs,
        IRunRepository runs,
        IAuditWriter audit,
        IClock clock,
        ILogger<MisfiredOccurrenceListener> logger)
    {
        _jobs = jobs;
        _runs = runs;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public string Name => nameof(MisfiredOccurrenceListener);

    public async Task TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default)
    {
        var raw = trigger.JobDataMap.TryGetString(SyncQuartzJob.JobIdKey, out var fromTrigger)
            ? fromTrigger
            : null;

        if (raw is null && trigger.JobKey is { } jobKey)
        {
            // Cron triggers carry no job data of their own; the id lives on the job detail, whose
            // key name is the job's GUID in "N" form (SyncJobScheduler.ScheduleJobAsync).
            raw = jobKey.Name;
        }

        if (!Guid.TryParse(raw, out var jobId))
        {
            _logger.LogWarning(
                "A scheduled occurrence was skipped for trigger {Trigger}, but its job id could not be read.",
                trigger.Key);
            return;
        }

        // Nothing here may throw: this runs on Quartz's scheduler thread, and a listener that
        // faults would take down the acquisition loop for every job, turning a reporting gap into
        // a total scheduling outage — a far worse failure than the one it is reporting.
        try
        {
            var job = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return; // deleted between the fire time and now; nothing to report against
            }

            var nextFire = trigger.GetNextFireTimeUtc();
            _logger.LogWarning(
                "Job {JobId} ({JobName}): a scheduled occurrence was skipped because no scheduler worker was "
                + "free within the misfire threshold. The next occurrence is {NextFire:u}. Repeated skips mean "
                + "too many jobs share one cron time — stagger them, or raise the scheduler's worker count.",
                job.Id, job.Name, nextFire);

            var at = _clock.UtcNow;
            var run = new SyncRun
            {
                RunKey = at.LocalDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture),
                JobId = job.Id,
                JobName = job.Name,
                Trigger = RunTrigger.Scheduled,
                TriggeredBy = CurrentActor.Name,
                Status = RunStatus.Skipped,
                ServerName = string.Empty,
                Databases = job.DatabaseScope == DatabaseScope.AllUserDatabases
                    ? "All user databases"
                    : string.Join(", ", job.Databases),
                StartedAt = at,
                CompletedAt = at,
                ErrorMessage =
                    "This scheduled occurrence was skipped: every scheduler worker was busy when it came due, "
                    + "and the occurrence was already past the misfire threshold by the time one freed up. "
                    + "The next occurrence runs normally. If this repeats for the same job, stagger the cron "
                    + "times of the jobs that share this slot.",
                Tags = [.. job.Tags],
            };

            await _runs.InsertAsync(run, cancellationToken).ConfigureAwait(false);

            // RunCompleted rather than RunFailed: nothing went wrong, the occurrence was dropped by
            // policy. The trail still answers "why did this job not run last night".
            await _audit.WriteAsync(
                AuditAction.RunCompleted, "Job", job.Id.ToString(), job.Name,
                $"Scheduled occurrence {run.RunKey} skipped — no scheduler worker was free in time.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Job {JobId}: a skipped occurrence could not be recorded.", jobId);
        }
    }

    public Task TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task TriggerComplete(
        ITrigger trigger,
        IJobExecutionContext context,
        SchedulerInstruction triggerInstructionCode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
