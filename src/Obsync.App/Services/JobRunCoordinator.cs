using System.Collections.Concurrent;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// Live, observable execution state for a single job. There is one shared instance per job, so every
/// screen (Dashboard, Jobs, Job Detail) sees the same "is running / what is it doing" state and it
/// survives navigation.
/// </summary>
public sealed partial class JobRunState : ObservableObject
{
    public JobRunState(Guid jobId) => JobId = jobId;

    public Guid JobId { get; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _message;
}

/// <summary>How a <see cref="IJobRunCoordinator.RunAsync"/> request ended.</summary>
public enum RunRequestStatus
{
    /// <summary>The run executed; <see cref="RunRequestOutcome.Run"/> holds the completed run.</summary>
    Started,

    /// <summary>A run of this job was already in progress; the request was ignored.</summary>
    AlreadyRunning,

    /// <summary>The user declined the production-run confirmation; nothing was started.</summary>
    Declined,
}

/// <summary>The outcome of a run request. <see cref="Run"/> is set only for <see cref="RunRequestStatus.Started"/>.</summary>
public sealed record RunRequestOutcome(RunRequestStatus Status, SyncRun? Run = null)
{
    public static readonly RunRequestOutcome AlreadyRunning = new(RunRequestStatus.AlreadyRunning);
    public static readonly RunRequestOutcome Declined = new(RunRequestStatus.Declined);
    public static RunRequestOutcome Started(SyncRun run) => new(RunRequestStatus.Started, run);
}

/// <summary>
/// The single owner of run execution for the desktop app. Guarantees a job runs at most once at a
/// time (no matter how many places try to start it), and exposes shared live state so the running
/// indicator and progress message persist across navigation.
/// </summary>
public interface IJobRunCoordinator
{
    /// <summary>True when a run for this job is currently in progress in the app.</summary>
    bool IsRunning(Guid jobId);

    /// <summary>True when any job is currently running in the app (close-with-running-run guard).</summary>
    bool HasActiveRuns { get; }

    /// <summary>The shared, observable run state for a job (created on first request).</summary>
    JobRunState GetState(Guid jobId);

    /// <summary>
    /// Runs the job if it is not already running. The outcome distinguishes a completed run from a
    /// declined production confirmation and from a request ignored because a run was in progress.
    /// </summary>
    Task<RunRequestOutcome> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of the job's in-flight run. A no-op when it is not running.</summary>
    void Cancel(Guid jobId);

    /// <summary>Raised (on the UI thread) when a run starts or finishes, so lists can refresh.</summary>
    event EventHandler<Guid>? RunStateChanged;

    /// <summary>Raised (on the UI thread) with the completed run — powers failure notifications.</summary>
    event EventHandler<SyncRun>? RunCompleted;
}

/// <inheritdoc cref="IJobRunCoordinator" />
public sealed class JobRunCoordinator : IJobRunCoordinator
{
    private readonly ISyncEngine _engine;
    private readonly IAuditWriter _audit;
    private readonly IProductionRunGuard _guard;
    private readonly ConcurrentDictionary<Guid, JobRunState> _states = new();
    private readonly HashSet<Guid> _running = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly object _gate = new();

    public JobRunCoordinator(ISyncEngine engine, IAuditWriter audit, IProductionRunGuard guard)
    {
        _engine = engine;
        _audit = audit;
        _guard = guard;
    }

    public event EventHandler<Guid>? RunStateChanged;
    public event EventHandler<SyncRun>? RunCompleted;

    public JobRunState GetState(Guid jobId) => _states.GetOrAdd(jobId, id => new JobRunState(id));

    public bool IsRunning(Guid jobId)
    {
        lock (_gate)
        {
            return _running.Contains(jobId);
        }
    }

    public bool HasActiveRuns
    {
        get
        {
            lock (_gate)
            {
                return _running.Count > 0;
            }
        }
    }

    public void Cancel(Guid jobId)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _cancellations.TryGetValue(jobId, out cancellation);
        }

        if (cancellation is null)
        {
            return;
        }

        // Message first: the engine keeps reporting progress until the token lands, and the progress
        // callback stops overwriting the message once cancellation is requested.
        var state = GetState(jobId);
        OnUi(() => state.Message = "Cancelling…");
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the lookup and the cancel — nothing left to stop.
        }
    }

    public async Task<RunRequestOutcome> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken = default)
    {
        // Manual runs against a production-tagged job need explicit confirmation. Done before the
        // concurrency slot is taken so declining leaves no state behind. Scheduled/startup runs and
        // the service host never flow through here, so they are never prompted.
        if (trigger == RunTrigger.Manual
            && !await _guard.ConfirmManualRunAsync(jobId, cancellationToken).ConfigureAwait(false))
        {
            return RunRequestOutcome.Declined;
        }

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            // The concurrency guard: refuse to start a second run of the same job. This is the single
            // choke point through which every "Run Now" flows, so it holds no matter which screen asks.
            if (!_running.Add(jobId))
            {
                return RunRequestOutcome.AlreadyRunning;
            }

            // Created inside the slot so Cancel(jobId) sees exactly one live source per running job.
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellations[jobId] = cancellation;
        }

        var token = cancellation.Token;
        var state = GetState(jobId);
        OnUi(() =>
        {
            state.IsRunning = true;
            state.Message = "Starting…";
        });
        RaiseOnUi(jobId);

        try
        {
            // Record who started a manual run. The engine audits every run's OUTCOME (all triggers,
            // both hosts) — this start event exists only for manual runs, where the user's intent
            // to run is itself the audited action.
            if (trigger == RunTrigger.Manual)
            {
                await _audit.WriteAsync(AuditAction.RunStarted, "Job", jobId.ToString(), null, "Manual run", token)
                    .ConfigureAwait(false);
            }

            // Once a cancel is requested, "Cancelling…" stays put — the engine keeps reporting
            // progress while it winds down, and those messages must not overwrite it.
            var progress = new Progress<SyncProgress>(p => OnUi(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    state.Message = p.Message;
                }
            }));

            // No scheduling token on Task.Run: cancellation must always reach the engine, which
            // honors it and records the run as Cancelled (rather than throwing out of this method).
            var run = await Task.Run(() => _engine.RunJobAsync(jobId, trigger, progress, token))
                .ConfigureAwait(false);

            var completed = RunCompleted;
            if (completed is not null)
            {
                OnUi(() => completed(this, run));
            }

            return RunRequestOutcome.Started(run);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(jobId);
                _cancellations.Remove(jobId);
            }

            cancellation.Dispose();
            OnUi(() =>
            {
                state.IsRunning = false;
                state.Message = null;
            });
            RaiseOnUi(jobId);
        }
    }

    // Run state changes must land on the UI thread: JobRunState is data-bound, and the engine reports
    // progress and completes on background threads.
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // BeginInvoke (not Invoke): never block the engine's background thread on the UI queue.
            dispatcher.BeginInvoke(action);
        }
    }

    private void RaiseOnUi(Guid jobId)
    {
        var handler = RunStateChanged;
        if (handler is not null)
        {
            OnUi(() => handler(this, jobId));
        }
    }
}
