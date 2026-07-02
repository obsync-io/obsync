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

/// <summary>
/// The single owner of run execution for the desktop app. Guarantees a job runs at most once at a
/// time (no matter how many places try to start it), and exposes shared live state so the running
/// indicator and progress message persist across navigation.
/// </summary>
public interface IJobRunCoordinator
{
    /// <summary>True when a run for this job is currently in progress in the app.</summary>
    bool IsRunning(Guid jobId);

    /// <summary>The shared, observable run state for a job (created on first request).</summary>
    JobRunState GetState(Guid jobId);

    /// <summary>
    /// Runs the job if it is not already running. Returns the completed run, or null when a run was
    /// already in progress (the request is ignored rather than starting a second concurrent run).
    /// </summary>
    Task<SyncRun?> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>Raised (on the UI thread) when a run starts or finishes, so lists can refresh.</summary>
    event EventHandler<Guid>? RunStateChanged;
}

/// <inheritdoc cref="IJobRunCoordinator" />
public sealed class JobRunCoordinator : IJobRunCoordinator
{
    private readonly ISyncEngine _engine;
    private readonly IAuditWriter _audit;
    private readonly ConcurrentDictionary<Guid, JobRunState> _states = new();
    private readonly HashSet<Guid> _running = [];
    private readonly object _gate = new();

    public JobRunCoordinator(ISyncEngine engine, IAuditWriter audit)
    {
        _engine = engine;
        _audit = audit;
    }

    public event EventHandler<Guid>? RunStateChanged;

    public JobRunState GetState(Guid jobId) => _states.GetOrAdd(jobId, id => new JobRunState(id));

    public bool IsRunning(Guid jobId)
    {
        lock (_gate)
        {
            return _running.Contains(jobId);
        }
    }

    public async Task<SyncRun?> RunAsync(Guid jobId, RunTrigger trigger, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // The concurrency guard: refuse to start a second run of the same job. This is the single
            // choke point through which every "Run Now" flows, so it holds no matter which screen asks.
            if (!_running.Add(jobId))
            {
                return null;
            }
        }

        var state = GetState(jobId);
        OnUi(() =>
        {
            state.IsRunning = true;
            state.Message = "Starting…";
        });
        RaiseOnUi(jobId);

        try
        {
            // Record who started a manual run. Scheduled runs are attributed via runs.triggered_by,
            // written by the engine — the coordinator only handles app-initiated (manual) runs.
            if (trigger == RunTrigger.Manual)
            {
                await _audit.WriteAsync(AuditAction.RunStarted, "Job", jobId.ToString(), null, "Manual run", cancellationToken)
                    .ConfigureAwait(false);
            }

            var progress = new Progress<SyncProgress>(p => OnUi(() => state.Message = p.Message));
            return await Task.Run(() => _engine.RunJobAsync(jobId, trigger, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(jobId);
            }

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
