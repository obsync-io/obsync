using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data;
using Obsync.Data.Repositories;
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

        MainViewModel mainViewModel;
        try
        {
            ObsyncPaths.EnsureCreated();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(System.IO.Path.Combine(ObsyncPaths.LogsRoot, "app-.log"), rollingInterval: RollingInterval.Day)
                .CreateLogger();

            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                    services.AddObsyncApp(ObsyncPaths.DatabasePath, ObsyncPaths.WorkspacesRoot))
                .Build();

            await _host.StartAsync();
            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

            // Reconcile orphaned runs: any run still "Running" after a restart never completed
            // (runs execute in-process), so clear the zombie rows the History would otherwise show.
            var now = DateTimeOffset.UtcNow;
            await _host.Services.GetRequiredService<IRunRepository>()
                .FailStaleRunningAsync(now.AddMinutes(-5), now, "Run interrupted — the app closed before it finished.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            window.DataContext = mainViewModel;
            window.Show();
        }
        catch (Exception ex)
        {
            // Startup failed before the window is up — surface it and exit rather than leaving a
            // windowless process alive (ShutdownMode.OnLastWindowClose would never fire).
            Log.Fatal(ex, "Obsync failed to start.");
            Views.AppDialog.Error(null, "Obsync — startup failed", Describe(ex));
            Shutdown(1);
            return;
        }

        // The window is shown; a failure here is non-fatal and handled by the dispatcher handler.
        // Triggered after construction so the dashboard (which depends on IShellNavigator == this
        // view model) is not resolved while the shell itself is still being built.
        await mainViewModel.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Views.AppDialog.Error(Current.MainWindow, "Obsync", Describe(e.Exception));
        Log.Error(e.Exception, "Unhandled UI exception.");
        e.Handled = true;
    }

    /// <summary>The innermost exception message — DI/reflection failures wrap the real cause.</summary>
    private static string Describe(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
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
