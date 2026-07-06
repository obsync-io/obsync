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

ObsyncPaths.EnsureCreated();

var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(ObsyncPaths.LogsRoot, "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31);

if (WindowsServiceHelpers.IsWindowsService())
{
    // Warnings and errors also land in the Windows Application event log for ops visibility. The
    // "Obsync" source is registered by the MSI (elevated), so the sink never has to create it —
    // console/dev runs skip the sink entirely and need no registration.
    loggerConfiguration = loggerConfiguration.WriteTo.EventLog(
        source: "Obsync",
        manageEventSource: false,
        restrictedToMinimumLevel: LogEventLevel.Warning);
}

Log.Logger = loggerConfiguration.CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "Obsync");
    builder.Services.AddSerilog();

    builder.Services.AddObsyncSecurity();
    builder.Services.AddObsyncCore(ObsyncPaths.DatabasePath, options =>
    {
        options.WorkspacesRoot = ObsyncPaths.WorkspacesRoot;
    });
    builder.Services.AddObsyncScheduler();

    builder.Services.AddQuartz();
    builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    builder.Services.AddHostedService<JobSchedulingBootstrapper>();
    // Keeps the live schedule in sync with the database so app changes apply without a restart.
    builder.Services.AddHostedService<JobReconciliationService>();
    // Prunes run history per the retention setting (startup + daily).
    builder.Services.AddHostedService<RunRetentionService>();

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Obsync service terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}
