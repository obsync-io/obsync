using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obsync.App.Services;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Lists sync jobs and runs them on demand.</summary>
public sealed partial class JobsViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IJobRunCoordinator _coordinator;
    private readonly IAuditWriter _audit;
    private readonly IAppSettingsRepository _settings;
    private readonly IJobConfigPorter _porter;
    private readonly ISchedulerHealthService _schedulerHealth;
    private readonly IClock _clock;

    private bool _reloading;

    [ObservableProperty] private string? _statusMessage;

    /// <summary>Set when jobs have active schedules the background service cannot execute.</summary>
    [ObservableProperty] private string? _schedulerWarning;

    public ObservableCollection<SyncJob> Jobs { get; } = [];

    public JobsViewModel(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IJobRunCoordinator coordinator,
        IAuditWriter audit,
        IAppSettingsRepository settings,
        IJobConfigPorter porter,
        ISchedulerHealthService schedulerHealth,
        IClock clock)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _coordinator = coordinator;
        _audit = audit;
        _settings = settings;
        _porter = porter;
        _schedulerHealth = schedulerHealth;
        _clock = clock;
        _coordinator.RunStateChanged += OnRunStateChanged;
    }

    /// <summary>Imports a job configuration exported from another machine (or a backup).</summary>
    [RelayCommand]
    private async Task ImportJobAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a job configuration",
            Filter = "Obsync job export (*.obsync-job.json)|*.obsync-job.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(dialog.FileName);
            var result = await _porter.ImportAsync(json);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error;
                return;
            }

            StatusMessage = $"Imported \"{result.Job!.Name}\" — review its configuration, then run or enable it.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed — {ex.Message}";
        }
    }

    private async void OnRunStateChanged(object? sender, Guid jobId)
    {
        RunNowCommand.NotifyCanExecuteChanged();
        if (_reloading)
        {
            return;
        }

        _reloading = true;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh — {ex.Message}";
        }
        finally
        {
            _reloading = false;
        }
    }

    public async Task LoadAsync()
    {
        // A stale action message must not outlive a reload (e.g. navigating away and back).
        StatusMessage = null;

        var now = _clock.UtcNow;
        var jobs = await _jobs.GetAllAsync();
        var markers = await _settings.GetProductionTagsAsync();
        await JobDisplay.PopulateAsync(jobs, _connections, _repositories, markers);
        Jobs.Clear();
        foreach (var job in jobs)
        {
            job.IsRunning = _coordinator.IsRunning(job.Id);
            job.IsOverdue = job.IsScheduleOverdue(now);
            Jobs.Add(job);
        }

        // Warn when a schedule exists that the background service cannot execute — otherwise the
        // "Next Run" column would quietly promise runs that will never happen.
        var health = jobs.Any(SchedulerHealthService.NeedsScheduler)
            ? await _schedulerHealth.GetAsync()
            : null;
        SchedulerWarning = health?.CanExecuteSchedules == false ? health.Summary : null;
    }

    // AllowConcurrentExecutions so different jobs can run at once; CanRun blocks a second run of the
    // SAME job, and the coordinator is the authoritative guard.
    [RelayCommand(CanExecute = nameof(CanRun), AllowConcurrentExecutions = true)]
    private async Task RunNowAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        try
        {
            var outcome = await _coordinator.RunAsync(job.Id, RunTrigger.Manual);
            if (outcome.Status == RunRequestStatus.AlreadyRunning)
            {
                StatusMessage = $"{job.Name}: a run is already in progress.";
            }
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the job is already running in another Obsync process (service/CLI run lock).
            StatusMessage = ex.Message;
        }
    }

    private bool CanRun(SyncJob? job) => job is not null && !_coordinator.IsRunning(job.Id);

    /// <summary>
    /// Pauses (disables) or resumes a job's schedule. Pausing clears the cached next-run time
    /// immediately so the table never advertises a run that will not happen; resuming previews the
    /// next occurrence (null for cron, whose exact fire time only the scheduler computes) — the
    /// service replaces the preview with the authoritative value on its next reconcile tick.
    /// </summary>
    [RelayCommand]
    private async Task TogglePauseAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        var pausing = job.Enabled;
        job.Enabled = !pausing;
        job.UpdatedAt = _clock.UtcNow;
        job.RunSummary.NextRunAt = pausing ? null : job.Schedule.GetNextRun(_clock.UtcNow);
        await _jobs.UpsertAsync(job);
        // UpsertAsync deliberately never touches the run summary — patch the cached next-run field
        // explicitly so the cleared (or previewed) value survives the reload below.
        await _jobs.UpdateNextRunAtAsync(job.Id, job.RunSummary.NextRunAt);
        await _audit.WriteAsync(
            pausing ? AuditAction.JobPaused : AuditAction.JobResumed, "Job", job.Id.ToString(), job.Name);
        await LoadAsync();
        StatusMessage = pausing
            ? $"{job.Name}: schedule paused — the job now runs only when you start it."
            : $"{job.Name}: schedule resumed.";
    }

    /// <summary>
    /// Creates a deep copy of a job under a unique "<c>name (copy)</c>" name. The copy starts
    /// paused with a blank run history so it can neither fire nor look like it already ran before
    /// the user reviews its schedule and destination.
    /// </summary>
    [RelayCommand]
    private async Task DuplicateAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        var copy = Clone(job);
        copy.Id = Guid.NewGuid();
        copy.Name = DuplicateName(job.Name, Jobs.Select(j => j.Name));
        copy.Enabled = false;
        copy.RunSummary = new JobRunSummary();
        copy.CreatedAt = copy.UpdatedAt = _clock.UtcNow;
        await _jobs.UpsertAsync(copy);
        await _audit.WriteAsync(AuditAction.JobDuplicated, "Job", copy.Id.ToString(), copy.Name, $"Copied from “{job.Name}”");
        await LoadAsync();
        StatusMessage = $"Created “{copy.Name}” — paused until you review its schedule.";
    }

    /// <summary>Exports a job's configuration as portable, secret-free JSON (the same flow as the
    /// Job Workspace's Configuration tab).</summary>
    [RelayCommand]
    private async Task ExportConfigAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        var message = await JobConfigExport.PromptAndWriteAsync(_porter, _audit, job);
        if (message is not null)
        {
            StatusMessage = message;
        }
    }

    /// <summary>A JSON round-trip clones every persisted field — including ones added later —
    /// without a hand-written copy that could silently miss additions.</summary>
    private static SyncJob Clone(SyncJob job)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<SyncJob>(System.Text.Json.JsonSerializer.Serialize(job))!;
        // System.Text.Json rebuilds the HashSet with the default (ordinal) comparer; restore
        // case-insensitivity the same way JobRepository does on read.
        copy.Selection.SchemaFilter = new HashSet<string>(copy.Selection.SchemaFilter, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>"name (copy)", or "name (copy N)" for the first N ≥ 2 whose result is not already
    /// taken (compared case-insensitively, matching SQL-side name semantics).</summary>
    internal static string DuplicateName(string name, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var candidate = $"{name} (copy)";
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{name} (copy {n})";
        }

        return candidate;
    }

    [RelayCommand]
    private async Task DeleteAsync(SyncJob? job)
    {
        if (job is null)
        {
            return;
        }

        // The coordinator only sees runs started in THIS app; the cross-process run lock is the
        // ground truth for a run executing in the scheduler service or the CLI.
        if (_coordinator.IsRunning(job.Id) || JobRunLock.IsHeld(ObsyncPaths.LocksRoot, job.Id))
        {
            StatusMessage = $"{job.Name}: cannot delete while a run is in progress.";
            return;
        }

        await _jobs.DeleteAsync(job.Id);
        await _audit.WriteAsync(AuditAction.JobDeleted, "Job", job.Id.ToString(), job.Name);
        await LoadAsync();
    }
}
