using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Shared;
using Obsync.Shared.Models;
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
        // DatabasesDisplay, not the raw list — an all-user-databases job has an empty list.
        Console.WriteLine($"{Truncate(job.Name, 32),-32} {job.RunSummary.LastStatus?.ToString() ?? "—",-12} {job.DatabasesDisplay}");
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

    // The two benign engine gates (disabled job, maintenance window) return an UN-PERSISTED run
    // with NoChanges, which is right for the scheduler — nothing went wrong, and a history row
    // would be noise. For automation it is not: `obsync run` is an explicit request to run THIS
    // job, so silently reporting success while doing nothing is the failure this exit code exists
    // to prevent. Checked here, ahead of the engine, purely so the message can name the reason;
    // the engine's own gates remain authoritative.
    if (!job.Enabled)
    {
        Console.Error.WriteLine(
            $"Job '{job.Name}' is disabled and was not run. Enable it in the Obsync app, or use Run Now there "
            + "to run it once without enabling it.");
        return 4;
    }

    if (!job.Schedule.IsWithinMaintenanceWindow(DateTimeOffset.Now))
    {
        Console.Error.WriteLine(
            $"Job '{job.Name}' is outside its maintenance window and was not run. Run it inside the window, "
            + "widen the window in the Obsync app, or use Run Now there to override.");
        return 4;
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
    SyncRun run;
    try
    {
        // Cli, NOT Manual. Manual disables the mass-deletion circuit breaker, the disabled-job gate
        // and the maintenance window, on the stated grounds that "the user is present and sees the
        // counts". Nobody is present for a Task Scheduler or CI invocation, and the counts are
        // printed below only AFTER the push — so a login that lost VIEW DEFINITION would have had
        // its whole schema deleted and pushed before anyone could read them.
        run = await engine.RunJobAsync(job.Id, RunTrigger.Cli, progress, cts.Token);
    }
    catch (InvalidOperationException ex)
    {
        // Configuration faults (missing repository/connection profile) still surface as exceptions.
        // The contention gates no longer do: under the Cli trigger they record the occurrence and
        // return it, which is why Skipped is mapped to its own exit code below.
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

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

    // Warning gets its own exit code (see help): the run partially succeeded (e.g. commit created
    // but push failed) and scripts checking "!= 0" should notice.
    //
    // Skipped must not be 0 either. Under the Manual trigger the contention gates THREW and this
    // method returned 1; under Cli they record the occurrence and return it, so without this arm a
    // job that never ran because another process held its lock would report success to the caller.
    return run.Status switch
    {
        RunStatus.Failed or RunStatus.Cancelled => 1,
        RunStatus.Warning => 3,
        RunStatus.Skipped => 4,
        _ => 0,
    };
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

        Exit codes (run):
          0   succeeded (or no changes)
          1   failed, cancelled, or could not start
          2   usage error
          3   finished with warnings (e.g. commit created but push failed)
          4   did not run (job disabled, outside its maintenance window, or already
              running in another Obsync process)

        CLI runs are unattended. The mass-deletion safety stop, the disabled-job gate and
        the maintenance window all apply, and none can be overridden from the command line:
        if a run's deletions are suspended, confirm them with Run Now in the Obsync app,
        where the counts are shown before anything is committed.
        """);
    return 0;
}

static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
