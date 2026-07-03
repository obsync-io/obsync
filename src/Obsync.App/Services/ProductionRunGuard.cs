using System.Windows;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// Confirms a manual run against a production-tagged job. Consulted only for manual (interactive)
/// runs by <see cref="JobRunCoordinator"/>; scheduled/service runs never reach it.
/// </summary>
public interface IProductionRunGuard
{
    /// <summary>
    /// Returns true when the manual run may proceed. Prompts (on the UI thread) only when the job
    /// carries a production tag; non-production jobs and unknown jobs proceed without asking.
    /// </summary>
    Task<bool> ConfirmManualRunAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IProductionRunGuard" />
public sealed class ProductionRunGuard : IProductionRunGuard
{
    private readonly IJobRepository _jobs;
    private readonly IAppSettingsRepository _settings;

    public ProductionRunGuard(IJobRepository jobs, IAppSettingsRepository settings)
    {
        _jobs = jobs;
        _settings = settings;
    }

    public async Task<bool> ConfirmManualRunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return true; // nothing to guard; the engine handles a missing job as it always has
        }

        var markers = await _settings.GetProductionTagsAsync(cancellationToken).ConfigureAwait(false);
        if (!JobTags.IsProduction(job.Tags, markers))
        {
            return true;
        }

        return ConfirmOnUi(job);
    }

    private static bool ConfirmOnUi(SyncJob job)
    {
        bool Show() => AppDialog.Confirm(
            Application.Current?.MainWindow,
            "Run against production?",
            $"'{job.Name}' is tagged for production ({string.Join(", ", job.Tags)}). Run it now?",
            "Run",
            destructive: true);

        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? Show() : dispatcher.Invoke(Show);
    }
}
