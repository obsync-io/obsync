using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Shared;
using Serilog;
using Serilog.Extensions.Logging;

ObsyncPaths.EnsureCreated();

using var serilog = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}")
    .CreateLogger();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(serilog)));
services.AddObsyncSecurity();
services.AddObsyncCore(ObsyncPaths.DatabasePath, options =>
{
    options.WorkspacesRoot = ObsyncPaths.WorkspacesRoot;
});

await using var provider = services.BuildServiceProvider();
await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

return command switch
{
    "list" or "jobs" => await ListJobsAsync(provider),
    "connections" => await ListConnectionsAsync(provider),
    "run" => await RunJobAsync(provider, args.Length > 1 ? args[1] : null),
    "version" => PrintVersion(),
    _ => PrintHelp(),
};

static async Task<int> ListJobsAsync(IServiceProvider provider)
{
    var jobs = await provider.GetRequiredService<IJobRepository>().GetAllAsync();
    if (jobs.Count == 0)
    {
        Console.WriteLine("No sync jobs yet. Create one in the Obsync app.");
        return 0;
    }

    Console.WriteLine($"{"NAME",-32} {"LAST STATUS",-12} {"DATABASES"}");
    foreach (var job in jobs)
    {
        Console.WriteLine($"{Truncate(job.Name, 32),-32} {job.RunSummary.LastStatus?.ToString() ?? "—",-12} {string.Join(", ", job.Databases)}");
    }

    return 0;
}

static async Task<int> ListConnectionsAsync(IServiceProvider provider)
{
    var connections = await provider.GetRequiredService<IConnectionProfileRepository>().GetAllAsync();
    foreach (var connection in connections)
    {
        Console.WriteLine($"{connection.Name,-32} {connection.ServerName}  ({connection.AuthenticationMode})");
    }

    return 0;
}

static async Task<int> RunJobAsync(IServiceProvider provider, string? jobReference)
{
    if (string.IsNullOrWhiteSpace(jobReference))
    {
        Console.Error.WriteLine("Usage: obsync run <job-name-or-id>");
        return 2;
    }

    var jobs = await provider.GetRequiredService<IJobRepository>().GetAllAsync();
    var job = Guid.TryParse(jobReference, out var id)
        ? jobs.FirstOrDefault(j => j.Id == id)
        : jobs.FirstOrDefault(j => string.Equals(j.Name, jobReference, StringComparison.OrdinalIgnoreCase));

    if (job is null)
    {
        Console.Error.WriteLine($"Job '{jobReference}' was not found.");
        return 1;
    }

    var engine = provider.GetRequiredService<ISyncEngine>();
    var progress = new Progress<SyncProgress>(p => Console.WriteLine($"  [{p.Phase}] {p.Message}"));

    // Ctrl+C requests a clean cancellation (the engine persists the run as Cancelled with its
    // logs) instead of hard-killing the process mid-run.
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.Error.WriteLine("Cancelling — waiting for the run to stop cleanly…");
        cts.Cancel();
    };

    Console.WriteLine($"Running job '{job.Name}'…");
    var run = await engine.RunJobAsync(job.Id, RunTrigger.Manual, progress, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"Status:   {run.Status}");
    Console.WriteLine($"Scanned:  {run.ObjectsScanned:N0}");
    Console.WriteLine($"Changes:  +{run.ObjectsAdded} ~{run.ObjectsModified} -{run.ObjectsDeleted}");
    Console.WriteLine($"Duration: {TimeSpan.FromMilliseconds(run.DurationMs):hh\\:mm\\:ss}");
    if (run.CommitUrl is not null)
    {
        Console.WriteLine($"Commit:   {run.CommitUrl}");
    }

    if (run.ErrorMessage is not null)
    {
        Console.Error.WriteLine($"Error:    {run.ErrorMessage}");
    }

    return run.Status is RunStatus.Failed or RunStatus.Cancelled ? 1 : 0;
}

static int PrintVersion()
{
    Console.WriteLine($"Obsync CLI {VersionInfo.Of(typeof(Program).Assembly)}");
    return 0;
}

static int PrintHelp()
{
    Console.WriteLine(
        """
        Obsync — automatically script, track, commit, and push SQL Server object changes to GitHub.

        Usage:
          obsync list                 List sync jobs and their last status
          obsync connections          List SQL Server connection profiles
          obsync run <name-or-id>     Run a sync job now
          obsync version              Show the CLI version
          obsync help                 Show this help
        """);
    return 0;
}

static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
