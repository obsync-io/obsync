using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Engine.DependencyInjection;
using Obsync.Security.DependencyInjection;

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

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<IShellNavigator>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<JobsViewModel>();
        services.AddSingleton<ConnectionsViewModel>();
        services.AddSingleton<RepositoriesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<CreateJobViewModel>();
        services.AddTransient<JobDetailViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
