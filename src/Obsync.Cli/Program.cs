using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.Security.DependencyInjection;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
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
    "credential" or "credentials" => await CredentialAsync(provider, args),
    "whoami" => PrintWhoAmI(),
    "version" => PrintVersion(),
    "help" or "--help" or "-h" or "/?" => PrintHelp(),

    // An unrecognised verb is a USAGE ERROR, not a request for help.
    //
    // This arm used to fall through to PrintHelp(), which returns 0 — so `obsync runn nightly`, or
    // any typo in a scheduled task's action, printed the help text and reported SUCCESS. A Task
    // Scheduler entry wrapping that mistake reports a healthy job for ever while nothing has run
    // since the day it was created. The documented exit-code table has always said 2 means a usage
    // error; this arm simply did not use it.
    _ => UnknownCommand(command),
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
    Console.WriteLine($"Changes:  +{run.ObjectsAdded} ~{run.ObjectsModified} -{run.ObjectsDeleted}"
        + (run.ObjectsRestored > 0 ? $" ({run.ObjectsRestored} restored)" : string.Empty));
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

/// <summary>
/// Stores, lists and deletes secrets in the CURRENT account's Windows Credential Manager vault.
/// </summary>
/// <remarks>
/// This exists because the vault is per-account and, until now, the only code in the product that
/// could write to it was the WPF app. That made the installer's own advice unperformable: it
/// recommends a group Managed Service Account ("enter DOMAIN\name$ and leave the password blank")
/// while also requiring that job credentials live in the SERVICE account's vault — and a gMSA has a
/// machine-managed password, so it cannot be signed in to, and the app cannot be run as it. The
/// same dead end applied to LocalSystem and every NT SERVICE / NT AUTHORITY account the wizard
/// accepts. Every such install ran a service that started, heartbeated, and reported itself healthy
/// while every git-mode run failed on a missing token.
/// <para>
/// A console command closes it, because a console CAN be run as those accounts — via
/// <c>psexec -s</c>, a scheduled task set to run as the gMSA, or <c>runas</c> for an ordinary
/// service account. See packaging/INSTALL.md.
/// </para>
/// <para>
/// Secrets are read from stdin or a hidden prompt, NEVER from the command line: Windows
/// process-creation auditing (Event 4688, Sysmon, EDR) records child command lines verbatim into
/// machine-wide security logs, which is the same reason the git layer passes tokens as environment
/// variables rather than <c>-c</c> arguments.
/// </para>
/// </remarks>
static async Task<int> CredentialAsync(IServiceProvider provider, string[] args)
{
    var action = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
    var credentials = provider.GetRequiredService<ICredentialStore>();

    // Always state the account. The whole class of failure this command addresses is a secret
    // written to the wrong vault, and that is invisible unless the tool says which one it used.
    Console.WriteLine($"Credential Manager vault: {CurrentActor.Name}");
    Console.WriteLine();

    if (action == "list")
    {
        return await ListCredentialsAsync(provider, credentials);
    }

    if (action == "prune")
    {
        return await PruneCredentialsAsync(provider, credentials, args.Contains("--yes", StringComparer.OrdinalIgnoreCase));
    }

    if (action is not ("set" or "delete"))
    {
        Console.Error.WriteLine("Usage: obsync credential <list|set|delete|prune> [github|sql|smtp|proxy] [name-or-id]");
        return 2;
    }

    var kind = args.Length > 2 ? args[2].ToLowerInvariant() : null;
    var reference = args.Length > 3 ? args[3] : null;

    string key;
    string description;
    switch (kind)
    {
        case "github":
        {
            var repositories = await provider.GetRequiredService<IRepositoryProfileRepository>().GetAllAsync();
            var match = Resolve(repositories, reference, r => r.Id, r => r.Name);
            if (match is null)
            {
                Console.Error.WriteLine(
                    reference is null
                        ? "Usage: obsync credential set github <repository-name-or-id>"
                        : $"Repository '{reference}' was not found. Run 'obsync credential list' to see the names.");
                // 2, not 1: this is a usage error, and its siblings in this same method already
                // return 2 for the identical condition. The documented table distinguishes them.
                return 2;
            }

            key = CredentialKeys.GitHubToken(match.Id);
            description = $"GitHub token for '{match.Name}' ({match.Owner}/{match.RepositoryName})";
            break;
        }

        case "sql":
        {
            var connections = await provider.GetRequiredService<IConnectionProfileRepository>().GetAllAsync();
            var match = Resolve(connections, reference, c => c.Id, c => c.Name);
            if (match is null)
            {
                Console.Error.WriteLine(
                    reference is null
                        ? "Usage: obsync credential set sql <server-name-or-id>"
                        : $"Server '{reference}' was not found. Run 'obsync credential list' to see the names.");
                // 2, not 1: this is a usage error, and its siblings in this same method already
                // return 2 for the identical condition. The documented table distinguishes them.
                return 2;
            }

            key = CredentialKeys.SqlPassword(match.Id);
            description = $"SQL password for '{match.Name}' ({match.ServerName})";
            break;
        }

        case "smtp":
            key = CredentialKeys.SmtpPassword();
            description = "SMTP password for email alerts";
            break;

        case "proxy":
            key = CredentialKeys.Proxy();
            description = "HTTP proxy password";
            break;

        default:
            Console.Error.WriteLine("The secret kind must be one of: github, sql, smtp, proxy.");
            return 2;
    }

    try
    {
        if (action == "delete")
        {
            credentials.Delete(key);
            Console.WriteLine($"Deleted the {description} from this account's vault.");
            return 0;
        }

        var secret = ReadSecret($"Enter the {description}");
        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("No value was entered — nothing was stored.");
            return 2;
        }

        credentials.Store(key, secret);

        // Read it back. Storing into a vault that cannot be read from is the exact failure this
        // command exists to prevent, and CredWrite succeeding does not prove CredRead will.
        if (credentials.Retrieve(key) != secret)
        {
            Console.Error.WriteLine("The secret was written but could not be read back — the vault is not usable.");
            return 1;
        }

        Console.WriteLine($"Stored the {description} in {CurrentActor.Name}'s vault and read it back successfully.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    static T? Resolve<T>(IReadOnlyList<T> items, string? reference, Func<T, Guid> id, Func<T, string> name)
        where T : class =>
        reference is null
            ? null
            : Guid.TryParse(reference, out var parsed)
                ? items.FirstOrDefault(i => id(i) == parsed)
                : items.FirstOrDefault(i => string.Equals(name(i), reference, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reports which secrets this account's vault holds — names and presence only, never values.</summary>
static async Task<int> ListCredentialsAsync(IServiceProvider provider, ICredentialStore credentials)
{
    var repositories = await provider.GetRequiredService<IRepositoryProfileRepository>().GetAllAsync();
    var connections = await provider.GetRequiredService<IConnectionProfileRepository>().GetAllAsync();

    Console.WriteLine($"{"SECRET",-46} {"KIND",-8} {"PRESENT"}");
    foreach (var repository in repositories)
    {
        Write(repository.Name, "github", CredentialKeys.GitHubToken(repository.Id));
    }

    foreach (var connection in connections.Where(c => c.RequiresPassword))
    {
        Write(connection.Name, "sql", CredentialKeys.SqlPassword(connection.Id));
    }

    Write("(email alerts)", "smtp", CredentialKeys.SmtpPassword());
    Write("(http proxy)", "proxy", CredentialKeys.Proxy());

    var orphans = FindOrphanedCredentials(credentials, repositories, connections);
    if (orphans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{orphans.Count} ORPHANED secret(s) — the profile that owned them is gone:");
        foreach (var key in orphans)
        {
            Console.WriteLine($"  {key}");
        }

        Console.WriteLine();
        Console.WriteLine("Remove them with: obsync credential prune");
    }

    Console.WriteLine();
    Console.WriteLine("Scheduled runs read these from the SERVICE account's vault, which is not this one");
    Console.WriteLine("unless the service runs as the account shown above.");
    return 0;

    void Write(string name, string kind, string key)
    {
        string present;
        try
        {
            present = credentials.Exists(key) ? "yes" : "no";
        }
        catch (Exception ex)
        {
            // An unreadable vault is the answer to the question being asked, not a reason to stop.
            present = $"error: {ex.Message}";
        }

        Console.WriteLine($"{Truncate(name, 46),-46} {kind,-8} {present}");
    }
}

/// <summary>
/// Secrets in this vault whose owning profile no longer exists.
/// </summary>
/// <remarks>
/// Obsync's keys embed the profile GUID, so once the row is deleted nothing in the product can name
/// the secret — a GitHub token with Contents:write could sit here indefinitely with no surface able
/// to list it, let alone remove it. The product CAUSES this: it tells users to store the same secret
/// under the service account as well, and deleting the profile in the app only ever removes the copy
/// in the signed-in user's vault.
/// </remarks>
static IReadOnlyList<string> FindOrphanedCredentials(
    ICredentialStore credentials,
    IReadOnlyList<GitRepositoryProfile> repositories,
    IReadOnlyList<SqlConnectionProfile> connections)
{
    IReadOnlyList<string> all;
    try
    {
        all = credentials.Enumerate("Obsync:");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not enumerate the vault — {ex.Message}");
        return [];
    }

    var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CredentialKeys.SmtpPassword(),
        CredentialKeys.Proxy(),
    };
    foreach (var repository in repositories)
    {
        live.Add(CredentialKeys.GitHubToken(repository.Id));
    }

    foreach (var connection in connections)
    {
        // Every connection, not just password ones: switching a profile to Windows auth already
        // deletes its secret, and treating the leftover as an orphan would race that.
        live.Add(CredentialKeys.SqlPassword(connection.Id));
    }

    return [.. all
        .Where(key => !live.Contains(key))
        // The diagnostics sentinel is written and deleted within one probe; a copy left by a crashed
        // probe is not an orphaned SECRET and must not be reported as one.
        .Where(key => !key.Equals("Obsync:diagnostic-probe", StringComparison.OrdinalIgnoreCase))
        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>Deletes every orphaned secret in this account's vault.</summary>
static async Task<int> PruneCredentialsAsync(IServiceProvider provider, ICredentialStore credentials, bool confirmed)
{
    var repositories = await provider.GetRequiredService<IRepositoryProfileRepository>().GetAllAsync();
    var connections = await provider.GetRequiredService<IConnectionProfileRepository>().GetAllAsync();
    var orphans = FindOrphanedCredentials(credentials, repositories, connections);

    if (orphans.Count == 0)
    {
        Console.WriteLine("No orphaned secrets — nothing to remove.");
        return 0;
    }

    foreach (var key in orphans)
    {
        Console.WriteLine($"  {key}");
    }

    if (!confirmed)
    {
        // Deleting a secret is irreversible, and this reads the SAME database the app does: run it
        // against the wrong data root and every live secret looks orphaned. Requiring --yes makes
        // that a deliberate act rather than a typo.
        Console.WriteLine();
        Console.WriteLine($"{orphans.Count} secret(s) would be deleted from {CurrentActor.Name}'s vault.");
        Console.WriteLine("Re-run with --yes to remove them. Check 'obsync whoami' first: this compares against the");
        Console.WriteLine("database at the data root shown there, so the wrong root would report live secrets as orphans.");
        return 0;
    }

    var removed = 0;
    foreach (var key in orphans)
    {
        try
        {
            credentials.Delete(key);
            removed++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not delete '{key}' — {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Removed {removed} of {orphans.Count} orphaned secret(s) from {CurrentActor.Name}'s vault.");
    return removed == orphans.Count ? 0 : 1;
}

/// <summary>
/// Reads a secret without echoing it and without ever putting it on a command line. Falls back to a
/// plain read when stdin is redirected, so the command works non-interactively — which is how it
/// runs under a scheduled task or <c>psexec -s</c>.
/// </summary>
static string ReadSecret(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    Console.Write($"{prompt}: ");
    var secret = new System.Text.StringBuilder();
    while (true)
    {
        var pressed = Console.ReadKey(intercept: true);
        if (pressed.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return secret.ToString();
        }

        if (pressed.Key == ConsoleKey.Backspace)
        {
            if (secret.Length > 0)
            {
                secret.Length--;
            }

            continue;
        }

        if (!char.IsControl(pressed.KeyChar))
        {
            secret.Append(pressed.KeyChar);
        }
    }
}

/// <summary>
/// Prints the Windows account this process runs as, and the data root that account resolves.
/// </summary>
/// <remarks>
/// Two lines, both of which are otherwise guesswork during a support call. The account decides which
/// Credential Manager vault a scheduled run reads; the data root decides which database it schedules
/// from. Run this under <c>psexec -s</c> and you can see exactly what the service sees.
/// </remarks>
static int PrintWhoAmI()
{
    Console.WriteLine($"Account:   {CurrentActor.Name}");
    Console.WriteLine($"Data root: {ObsyncPaths.Root}");
    if (ObsyncPaths.RootResolutionWarning is { } warning)
    {
        Console.WriteLine();
        Console.Error.WriteLine($"Warning: {warning}");
    }

    return 0;
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
          obsync whoami               Show the Windows account and data root in use
          obsync credential list      Show which secrets this account's vault holds
          obsync credential set <kind> [name-or-id]
          obsync credential delete <kind> [name-or-id]
                                      kind: github | sql | smtp | proxy
          obsync credential prune [--yes]
                                      Remove secrets whose profile was deleted
          obsync version              Show the CLI version
          obsync help                 Show this help

        Credentials are stored per Windows account, and SCHEDULED runs read them from the
        account the Obsync service runs as. To store them for a service account that cannot
        be signed in to — a gMSA, LocalSystem, or an NT SERVICE account — run this command
        AS that account:

          psexec -s obsync credential set github "My Repo"        (LocalSystem)
          schtasks /create /ru DOMAIN\gmsa$ /tr "obsync credential set ..."   (gMSA)

        The value is read from stdin or a hidden prompt, never from the command line, so it
        does not reach Windows process-creation auditing. Verify with:

          psexec -s obsync credential list

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

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"obsync: '{command}' is not an obsync command.");
    Console.Error.WriteLine();
    _ = PrintHelp();
    return 2;
}

static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
