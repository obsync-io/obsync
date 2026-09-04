using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Obsync.Engine.DependencyInjection;
using Obsync.Scheduler.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Service;
using Obsync.Shared;
using Quartz;
using Serilog;
using Serilog.Events;

// A bootstrap logger BEFORE anything touches a path. Creating the data directories can fail (an
// OBSYNC_DATA_ROOT on a disconnected volume, a denied ACL), and Serilog's default logger is a
// silent no-op — so doing this after the file sink was configured meant such a failure produced no
// log file, no event-log entry, and no Log.Fatal. The SCM's "did not respond to the start request
// in a timely fashion" was the only trace. The event-log sink is what makes it visible for a
// service, and neither sink here depends on ObsyncPaths.
var bootstrapLogger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console();
if (WindowsServiceHelpers.IsWindowsService())
{
    bootstrapLogger = bootstrapLogger.WriteTo.EventLog(
        source: "Obsync", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning);
}

Log.Logger = bootstrapLogger.CreateLogger();

// Non-zero so the SCM treats a fatal crash as a failure and applies the recovery actions the MSI
// configures (restart after 60s, three times).
const int FatalExitCode = 1;

// Declared outside the try so the catch can report the failure to the SCM, and the finally can
// dispose, even when the host threw while starting.
IHost? host = null;

try
{
    ObsyncPaths.EnsureCreated();

    // Now that the log directory exists, swap in the full logger with the rolling file sink.
    var loggerConfiguration = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(ObsyncPaths.LogsRoot, "service-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31);

    if (WindowsServiceHelpers.IsWindowsService())
    {
        // Warnings and errors also land in the Windows Application event log for ops visibility.
        // The "Obsync" source is registered by the MSI (elevated), so the sink never has to create
        // it — console/dev runs skip the sink entirely and need no registration.
        loggerConfiguration = loggerConfiguration.WriteTo.EventLog(
            source: "Obsync",
            manageEventSource: false,
            restrictedToMinimumLevel: LogEventLevel.Warning);
    }

    Log.Logger = loggerConfiguration.CreateLogger();

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "Obsync");

    // The 30s default has to cover the in-flight run drain AND Quartz's WaitForJobsToComplete, and
    // the drain alone could consume all of it. One budget shared by every hosted service, so it is
    // set here rather than being divided up implicitly by stop order.
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));
    builder.Services.AddSerilog();

    builder.Services.AddObsyncSecurity();
    builder.Services.AddObsyncCore(ObsyncPaths.DatabasePath, options =>
    {
        options.WorkspacesRoot = ObsyncPaths.WorkspacesRoot;
    });
    builder.Services.AddObsyncScheduler();

    builder.Services.AddQuartz();
    builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    // Registered AFTER the Quartz hosted service on purpose: hosted services stop in reverse
    // registration order, so this cancels in-flight runs BEFORE Quartz waits for them.
    builder.Services.AddHostedService<RunCancellationOnStopService>();
    builder.Services.AddHostedService<JobSchedulingBootstrapper>();
    // Keeps the live schedule in sync with the database so app changes apply without a restart.
    builder.Services.AddHostedService<JobReconciliationService>();
    // Prunes run history per the retention setting (startup + daily).
    builder.Services.AddHostedService<RunRetentionService>();

    // RunAsync is deliberately expanded here. It disposes the host in its own finally, and
    // disposing the host disposes WindowsServiceLifetime — a ServiceBase, which reports
    // SERVICE_STOPPED to the SCM using whatever ExitCode is set at that moment. Setting an exit
    // code in a catch around RunAsync is therefore always too late: the SCM has already recorded a
    // clean stop, and it keys the installer's restart-on-failure recovery off the *service* exit
    // code, not the process one. Splitting the run lets the failure be reported before disposal.
    host = builder.Build();
    await host.StartAsync();
    await host.WaitForShutdownAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Obsync service terminated unexpectedly.");

    // A fatal crash must not look like a clean stop to the SCM, or the installer's
    // restart-on-failure recovery never fires. Under a console/dev run IHostLifetime is a
    // ConsoleLifetime rather than a ServiceBase, so this simply does not apply.
    if (host?.Services.GetService<IHostLifetime>() is ServiceBase serviceLifetime)
    {
        serviceLifetime.ExitCode = FatalExitCode;
    }

    Environment.ExitCode = FatalExitCode;
}
finally
{
    if (host is IAsyncDisposable asyncDisposableHost)
    {
        await asyncDisposableHost.DisposeAsync();
    }
    else
    {
        host?.Dispose();
    }

    await Log.CloseAndFlushAsync();
}
