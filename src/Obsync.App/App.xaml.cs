using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data;
using Obsync.Engine.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Shared;
using Serilog;

namespace Obsync.App;

/// <summary>WPF application bootstrap: builds the host, runs migrations, and shows the main window.</summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>The composition root, for view code-behind that needs to resolve a transient dialog view model.</summary>
    public static IServiceProvider Services => ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The application host is not initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ObsyncPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(System.IO.Path.Combine(ObsyncPaths.LogsRoot, "app-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddObsyncSecurity();
                services.AddObsyncCore(ObsyncPaths.DatabasePath, options =>
                {
                    options.WorkspacesRoot = ObsyncPaths.WorkspacesRoot;
                });

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<JobsViewModel>();
                services.AddSingleton<ConnectionsViewModel>();
                services.AddSingleton<RepositoriesViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddTransient<CreateJobViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = _host.Services.GetRequiredService<MainViewModel>();
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Obsync", MessageBoxButton.OK, MessageBoxImage.Error);
        Log.Error(e.Exception, "Unhandled UI exception.");
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
