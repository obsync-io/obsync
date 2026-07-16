using Microsoft.Extensions.DependencyInjection;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.Engine.DependencyInjection;
using Obsync.Git;
using Obsync.Security.DependencyInjection;
using Obsync.Shared.Abstractions;

namespace Obsync.App;

/// <summary>
/// The desktop application's composition root. Kept separate from <see cref="App"/> so the service
/// graph can be built and validated in a test without standing up the WPF host.
/// </summary>
public static class AppServices
{
    public static IServiceCollection AddObsyncApp(this IServiceCollection services, string databasePath, string workspacesRoot)
    {
        services.AddObsyncSecurity();
        services.AddObsyncCore(databasePath, options => options.WorkspacesRoot = workspacesRoot);

        // Single owner of run execution: per-job concurrency guard + shared live state across screens.
        services.AddSingleton<IJobRunCoordinator, JobRunCoordinator>();

        // Confirms a manual run against a production-tagged job (keyed off the configurable markers).
        services.AddSingleton<IProductionRunGuard, ProductionRunGuard>();

        // Supportability: environment health checks and the secret-masked support bundle.
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        // "Will my schedules actually run?" — SCM state + service account + scheduler heartbeat.
        services.AddSingleton<ISchedulerHealthService, SchedulerHealthService>();
        services.AddSingleton<ISupportBundleWriter, SupportBundleWriter>();
        // Optional pre-save checks on the wizard's Review step (advisory — never blocks Save).
        services.AddSingleton<IJobPreflightService, JobPreflightService>();
        services.AddSingleton<IRunReportWriter, RunReportWriter>();

        // Reads before/after script content for the diff viewer from the local git workspaces.
        services.AddSingleton<IScriptHistoryService>(sp => new ScriptHistoryService(
            sp.GetRequiredService<IGitCommandRunner>(),
            sp.GetRequiredService<IAppSettingsRepository>(),
            workspacesRoot));

        // Secret-free job configuration export/import (profiles referenced by name, never embedded).
        services.AddSingleton<IJobConfigPorter, JobConfigPorter>();

        // Notify-only release check against GitHub (startup toast + Settings → About button).
        services.AddSingleton<IUpdateChecker, UpdateChecker>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<JobsViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<RepositoriesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<CreateJobViewModel>();
        services.AddTransient<ServerDialogViewModel>();
        services.AddTransient<RepositoryDialogViewModel>();
        services.AddTransient<JobDetailViewModel>();
        services.AddTransient<DependencyExplorerViewModel>();
        services.AddTransient<ScriptDiffViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
