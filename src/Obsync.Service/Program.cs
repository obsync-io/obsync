using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obsync.Engine.DependencyInjection;
using Obsync.Scheduler.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Service;
using Obsync.Shared;
using Quartz;
using Serilog;

ObsyncPaths.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(ObsyncPaths.LogsRoot, "service-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

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
