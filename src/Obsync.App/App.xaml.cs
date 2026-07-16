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

        // Failures on background threads never reach DispatcherUnhandledException; log them so a
        // crash outside the UI thread is diagnosable. No dialogs here — these can fire off the UI
        // thread (and, for AppDomain, while the process is already terminating).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception,
                "Unhandled background exception (terminating: {IsTerminating}).", args.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        MainViewModel mainViewModel;
        try
        {
            ObsyncPaths.EnsureCreated();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    System.IO.Path.Combine(ObsyncPaths.LogsRoot, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31)
                .CreateLogger();

            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                    services.AddObsyncApp(ObsyncPaths.DatabasePath, ObsyncPaths.WorkspacesRoot))
                .Build();

            await _host.StartAsync();
            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

            // Reconcile orphaned runs: a "Running" row whose job lock is no longer held belongs to a
            // process that died mid-run. Lock-probing (not an age cutoff) means a long run currently
            // executing in the scheduler service is never falsely marked failed.
            var now = DateTimeOffset.UtcNow;
            var runs = _host.Services.GetRequiredService<IRunRepository>();
            var recovered = await OrphanedRunCleaner.CleanAsync(runs, ObsyncPaths.LocksRoot, now);

            // Crash-recovered failures still alert — the process that ran them died before it
            // could. Best-effort, like the engine's own post-run alerting.
            var alerts = _host.Services.GetRequiredService<Engine.Alerting.IRunAlertService>();
            foreach (var run in recovered)
            {
                try
                {
                    await alerts.NotifyAsync(run, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not send the failure alert for recovered run {RunKey}.", run.RunKey);
                }
            }

            // Apply the run-history retention setting (0 = keep forever). The service also prunes
            // daily; doing it here keeps app-only installs tidy too.
            await RunRetention.CleanupAsync(
                _host.Services.GetRequiredService<IAppSettingsRepository>(), runs, now);

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
