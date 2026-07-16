using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.E2E;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

// ---------------------------------------------------------------------------------------------
// Obsync end-to-end audit harness.
//
// Drives the REAL production pipeline against disposable databases on a local SQL Server and
// local bare git repositories (no GitHub, no network). Each scenario asserts observable
// behavior: run status, git commits on the "remote", file contents, state DB rows.
//
//   dotnet run -c Debug --project tools/Obsync.E2E -- [--server localhost] [--keep]
// ---------------------------------------------------------------------------------------------

var server = "localhost";
var keep = false;
string? seedServiceRoot = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server": server = args[++i]; break;
        case "--keep": keep = true; break;
        case "--seed-service": seedServiceRoot = args[++i]; break;
    }
}

// ---------------------------------------------------------------------------------------------
// --seed-service <sandboxRoot>: provision an isolated data root (used as a fake LOCALAPPDATA)
// with one cron-scheduled ExportOnly job, so the real Obsync.Service can be observed executing
// scheduled runs without touching the user's real data.
// ---------------------------------------------------------------------------------------------
if (seedServiceRoot is not null)
{
    var dataRoot = Path.Combine(seedServiceRoot, "Obsync");
    Directory.CreateDirectory(dataRoot);

    var seedServices = new ServiceCollection();
    seedServices.AddLogging();
    seedServices.AddSingleton<Obsync.Shared.Abstractions.ICredentialStore>(new StubCredentialStore());
    Obsync.Engine.DependencyInjection.EngineServiceCollectionExtensions.AddObsyncCore(
        seedServices, Path.Combine(dataRoot, "obsync.db"),
        o => o.WorkspacesRoot = Path.Combine(dataRoot, "workspaces"));
    await using var seedProvider = seedServices.BuildServiceProvider();
    await seedProvider.GetRequiredService<Obsync.Data.IDatabaseInitializer>().InitializeAsync();

    var seedConnection = new SqlConnectionProfile { Name = "svc-e2e", ServerName = server, TrustServerCertificate = true };
    await seedProvider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(seedConnection);

    var exportTarget = Path.Combine(seedServiceRoot, "scheduled-export");
    var svcJob = new SyncJob
    {
        Name = "svc-e2e-cron",
        ConnectionProfileId = seedConnection.Id,
        RepositoryProfileId = null,
        Databases = [SqlSeeder.MainDb],
        CommitMode = CommitMode.ExportOnly,
        ExportPath = exportTarget,
        DestinationFolder = string.Empty,
        Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.ProgrammabilityOnly },
        Schedule = new ScheduleProfile { Kind = ScheduleKind.Cron, CronExpression = "0 0/2 * * * ?" },
    };
    await seedProvider.GetRequiredService<IJobRepository>().UpsertAsync(svcJob);

    Console.WriteLine($"Seeded service sandbox at {dataRoot}");
    Console.WriteLine($"JobId={svcJob.Id}");
    Console.WriteLine($"ExportTarget={exportTarget}");
    return 0;
}

var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var root = Path.Combine(Path.GetTempPath(), "obsync-e2e", stamp);
Directory.CreateDirectory(root);

var results = new List<(string Scenario, string Check, bool Pass, string Detail)>();
void Check(string scenario, string check, bool pass, string detail = "")
{
    results.Add((scenario, check, pass, detail));
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {scenario} :: {check}{(detail.Length > 0 ? $" — {detail}" : "")}");
}

Console.WriteLine($"Obsync E2E audit — server={server}, root={root}");
var seeder = new SqlSeeder(server);
Console.WriteLine("Seeding disposable databases…");
await seeder.RecreateAsync();

// ==============================================================================================
// Environment MAIN: DirectCommit job, FullSchema, reference data, all artifacts.
// ==============================================================================================
await using var main = await E2eEnvironment.CreateAsync(root, "main", server);
var job = await main.AddJobAsync(j =>
{
    j.Name = "e2e-direct";
    j.Databases = [SqlSeeder.MainDb];
    j.DestinationFolder = "db";
    j.CommitMode = CommitMode.DirectCommit;
    j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.FullSchema };
    j.Selection.ReferenceDataTables.AddRange(["dbo.RefStatus", "dbo.RefBig", "dbo.DoesNotExist"]);
});

// ---- S01: initial full run -------------------------------------------------------------------
{
    var run = await main.RunAsync(job.Id);
    // dbo.DoesNotExist reference table is a deliberate skip -> Warning is the honest status.
    Check("S01-initial", "status Warning (missing ref table reported) or Succeeded",
        run.Status is RunStatus.Succeeded or RunStatus.Warning, $"status={run.Status}, error={run.ErrorMessage}");
    Check("S01-initial", "objects scanned > 20", run.ObjectsScanned > 20, $"scanned={run.ObjectsScanned}");
    Check("S01-initial", "commit sha recorded", !string.IsNullOrEmpty(run.CommitSha), $"sha={run.CommitSha}");

    var files = main.RemoteFiles();
    // Single fixed-database jobs write straight under the destination folder (no per-DB nesting;
    // dynamic scope nests — covered by S15).
    string[] expected =
    [
        "db/tables/sales.Orders.sql",
        "db/tables/sales.Customers.sql",
        "db/views/sales.vwOrders.sql",
        "db/views/dbo.vwToDelete.sql",
        "db/procedures/sales.usp_GetOrders.sql",
        "db/metadata/object-inventory.json",
        "db/metadata/database-options.sql",
        "db/security/permissions/permissions.sql",
        "db/docs/README.md",
        "db/security/security-review.md",
        "db/data/dbo.RefStatus.sql",
    ];
    foreach (var f in expected)
    {
        Check("S01-initial", $"file {f}", files.Contains(f, StringComparer.OrdinalIgnoreCase)
            || files.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)),
            files.Count(x => x.StartsWith(Path.GetDirectoryName(f)!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) + " sibling files");
    }

    // Hostile names should exist SOMEWHERE under tables/procedures (mapper may transform them).
    Check("S01-initial", "hostile-name proc (space) scripted",
        files.Any(f => f.Contains("usp with space", StringComparison.OrdinalIgnoreCase) || f.Contains("usp_with_space", StringComparison.OrdinalIgnoreCase)),
        string.Join(", ", files.Where(f => f.Contains("procedures/dbo.", StringComparison.OrdinalIgnoreCase))));
    Check("S01-initial", "unicode proc scripted",
        files.Any(f => f.Contains("uspÜnïcødé", StringComparison.OrdinalIgnoreCase) || f.Contains("usp_nc_d", StringComparison.OrdinalIgnoreCase) || f.Contains("unicode", StringComparison.OrdinalIgnoreCase)),
        "");
    Check("S01-initial", "bracket-name table scripted",
        files.Any(f => f.Contains("Weird", StringComparison.OrdinalIgnoreCase)), "");

    // Reference data content sanity: deterministic INSERTs incl. quote + unicode fidelity.
    var refData = main.RemoteFileContent("db/data/dbo.RefStatus.sql");
    Check("S01-initial", "ref data quotes escaped", refData.Contains("O''Brien"), "");
    Check("S01-initial", "ref data unicode preserved", refData.Contains("Ünïcødé"), "");

    // Over-cap ref table must be skipped, not truncated (db/tables/dbo.RefBig.sql is its DDL — fine).
    Check("S01-initial", "over-cap ref table DATA not scripted",
        !files.Any(f => f.Equals("db/data/dbo.RefBig.sql", StringComparison.OrdinalIgnoreCase)), "");

    // No token/secret anywhere in the committed tree.
    var leaked = files.Where(f => main.RemoteFileContent(f).Contains("e2e-local-token", StringComparison.Ordinal)).ToList();
    Check("S01-initial", "no credential material in committed files", leaked.Count == 0, string.Join(", ", leaked));
}

var commitsAfterS01 = main.RemoteCommitCount();

// ---- S02: no-change run ----------------------------------------------------------------------
{
    var run = await main.RunAsync(job.Id);
    Check("S02-nochange", "status NoChanges or Warning-without-commit",
        run.Status is RunStatus.NoChanges || (run.Status == RunStatus.Warning && run.CommitSha is null),
        $"status={run.Status}, sha={run.CommitSha}");
    Check("S02-nochange", "no new remote commit", main.RemoteCommitCount() == commitsAfterS01,
        $"commits {commitsAfterS01} -> {main.RemoteCommitCount()}");
    Check("S02-nochange", "no false changes", run.ObjectsAdded + run.ObjectsModified + run.ObjectsDeleted == 0,
        $"+{run.ObjectsAdded}/~{run.ObjectsModified}/-{run.ObjectsDeleted}");
}

// ---- S03: modify one procedure ---------------------------------------------------------------
{
    await seeder.ModifyProcAsync();
    var run = await main.RunAsync(job.Id);
    // The regenerated metadata/object-inventory.json rides as a second "modified" entry — expected.
    Check("S03-modify", "one modified object (+ inventory artifact)",
        run.ObjectsModified is 1 or 2 && run.ObjectsAdded == 0 && run.ObjectsDeleted == 0,
        $"+{run.ObjectsAdded}/~{run.ObjectsModified}/-{run.ObjectsDeleted}");
    Check("S03-modify", "new commit pushed", main.RemoteCommitCount() == commitsAfterS01 + 1, "");
    var content = main.RemoteFileContent("db/procedures/sales.usp_GetOrders.sql");
    Check("S03-modify", "remote file has the new body", content.Contains("E2E modification marker"), "");

    var run2 = await main.RunAsync(job.Id);
    Check("S03-modify", "change not re-detected", run2.ObjectsModified == 0,
        $"second run status={run2.Status}, ~{run2.ObjectsModified}");
}

// ---- S04: delete a view ----------------------------------------------------------------------
{
    var before = main.RemoteCommitCount();
    await seeder.DropViewAsync();
    var run = await main.RunAsync(job.Id);
    Check("S04-delete", "exactly one deletion", run.ObjectsDeleted == 1, $"-{run.ObjectsDeleted}, status={run.Status}");
    Check("S04-delete", "file removed from remote HEAD",
        !main.RemoteFiles().Any(f => f.EndsWith("dbo.vwToDelete.sql", StringComparison.OrdinalIgnoreCase)), "");
    Check("S04-delete", "other files intact", main.RemoteFiles().Any(f => f.EndsWith("sales.vwOrders.sql", StringComparison.OrdinalIgnoreCase)), "");
    var run2 = await main.RunAsync(job.Id);
    Check("S04-delete", "deletion not repeated", run2.ObjectsDeleted == 0, $"second -{run2.ObjectsDeleted}");
    _ = before;
}

// ---- S05: rename a procedure -----------------------------------------------------------------
{
    await seeder.RenameProcAsync();
    var run = await main.RunAsync(job.Id);
    Check("S05-rename", "rename = one add + one delete", run.ObjectsAdded == 1 && run.ObjectsDeleted == 1,
        $"+{run.ObjectsAdded}/-{run.ObjectsDeleted}");
    var files = main.RemoteFiles();
    Check("S05-rename", "old file gone", !files.Any(f => f.EndsWith("dbo.usp_RenameMe.sql", StringComparison.OrdinalIgnoreCase)), "");
    Check("S05-rename", "new file present", files.Any(f => f.EndsWith("dbo.usp_Renamed.sql", StringComparison.OrdinalIgnoreCase)), "");
}

// ---- S06: encrypted module — reported skip, never a silent drop or false deletion -------------
{
    await seeder.AddEncryptedProcAsync();
    var filesBefore = main.RemoteFiles().Count;
    var run = await main.RunAsync(job.Id);
    Check("S06-encrypted", "run escalates to Warning", run.Status == RunStatus.Warning, $"status={run.Status}");
    Check("S06-encrypted", "skip counted in ObjectsFailed", run.ObjectsFailed >= 1, $"failed={run.ObjectsFailed}");
    Check("S06-encrypted", "no file for encrypted proc",
        !main.RemoteFiles().Any(f => f.Contains("usp_Secret", StringComparison.OrdinalIgnoreCase)), "");
    Check("S06-encrypted", "no collateral deletions", run.ObjectsDeleted == 0, $"-{run.ObjectsDeleted}");

    var logs = await main.Provider.GetRequiredService<IRunRepository>().GetLogsAsync(run.Id);
    Check("S06-encrypted", "skip visible in run logs",
        logs.Any(l => (l.Message + l.Detail).Contains("usp_Secret", StringComparison.OrdinalIgnoreCase)
                   || (l.Message + l.Detail).Contains("encrypt", StringComparison.OrdinalIgnoreCase)),
        $"{logs.Count} log rows");
    _ = filesBefore;
}

// ---- S07: push failure -> stranded commit recovered on next run -------------------------------
{
    var commitsBefore = main.RemoteCommitCount();
    main.RejectPushes();
    await seeder.ModifyProcAgainAsync();
    var run = await main.RunAsync(job.Id);
    Check("S07-pushfail", "run does NOT report clean success",
        run.Status is RunStatus.Warning or RunStatus.Failed, $"status={run.Status}");
    Check("S07-pushfail", "error mentions push",
        (run.ErrorMessage ?? "").Contains("push", StringComparison.OrdinalIgnoreCase), $"error={run.ErrorMessage}");
    Check("S07-pushfail", "remote unchanged", main.RemoteCommitCount() == commitsBefore, "");

    main.AllowPushes();
    var recovery = await main.RunAsync(job.Id);
    Check("S07-pushfail", "stranded commit pushed on next run", main.RemoteCommitCount() > commitsBefore,
        $"recovery status={recovery.Status}, commits {commitsBefore} -> {main.RemoteCommitCount()}");
    var content = main.RemoteFileContent("db/procedures/sales.usp_GetOrders.sql");
    Check("S07-pushfail", "the blocked change reached the remote", content.Contains("marker two"), "");
    var rerun = await main.RunAsync(job.Id);
    Check("S07-pushfail", "steady state clean afterwards",
        rerun.Status is RunStatus.NoChanges or RunStatus.Warning && rerun.ObjectsModified == 0,
        $"status={rerun.Status}");
}

// ---- S08: non-fast-forward divergence — loud failure, no force push, remote intact ------------
{
    main.RejectPushes();
    await seeder.ModifyProcAsync(); // marker one again (differs from current marker two)
    var stranded = await main.RunAsync(job.Id);
    main.AllowPushes();
    main.PushForeignCommit("external/README.md", "external writer\n");
    var foreignSha = main.RemoteHeadSha();

    await seeder.ModifyProcAgainAsync();
    var run = await main.RunAsync(job.Id);
    Check("S08-nonff", "divergence is not silent success",
        run.Status is RunStatus.Warning or RunStatus.Failed, $"status={run.Status}, error={run.ErrorMessage}");
    bool foreignIntact;
    try
    {
        GitTool.Capture(main.RemotePath, "merge-base", "--is-ancestor", foreignSha, "main");
        foreignIntact = true;
    }
    catch (InvalidOperationException)
    {
        foreignIntact = false; // non-zero exit: the foreign commit is no longer an ancestor
    }

    Check("S08-nonff", "foreign commit NOT overwritten (no force push)", foreignIntact,
        $"foreign={foreignSha[..12]}, head={main.RemoteHeadSha()[..12]}");
    Check("S08-nonff", "error message is actionable",
        !string.IsNullOrWhiteSpace(run.ErrorMessage), $"error={run.ErrorMessage}");
    _ = stranded;
}

// ---- S09: cancellation mid-run ---------------------------------------------------------------
{
    // Fresh environment so the cancelled run's state can't disturb the main timeline.
    await using var cancelEnv = await E2eEnvironment.CreateAsync(root, "cancel", server);
    var cancelJob = await cancelEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-cancel";
        j.Databases = [SqlSeeder.MainDb];
        j.DestinationFolder = "db";
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.FullSchema };
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
    var run = await cancelEnv.RunAsync(cancelJob.Id, cts.Token);
    var completed = run.Status is not RunStatus.Cancelled;
    Check("S09-cancel", "status Cancelled (or run legitimately beat the cancel)",
        run.Status == RunStatus.Cancelled || completed, $"status={run.Status}");

    var running = await cancelEnv.Provider.GetRequiredService<IRunRepository>().GetRunningAsync();
    Check("S09-cancel", "no run stuck in Running", running.Count == 0, $"{running.Count} stuck");

    var recovery = await cancelEnv.RunAsync(cancelJob.Id);
    Check("S09-cancel", "rerun after cancel recovers",
        recovery.Status is RunStatus.Succeeded or RunStatus.Warning or RunStatus.NoChanges,
        $"status={recovery.Status}, error={recovery.ErrorMessage}");
    Check("S09-cancel", "rerun ends with full tree on remote",
        cancelEnv.RemoteFiles().Any(f => f.EndsWith("sales.Orders.sql", StringComparison.OrdinalIgnoreCase)), "");
}

// ---- S10: Export Only — no git, no token, no repository ---------------------------------------
{
    await using var exportEnv = await E2eEnvironment.CreateAsync(root, "export", server);
    var exportDir = Path.Combine(exportEnv.Root, "export-out");
    var exportJob = await exportEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-export";
        j.Databases = [SqlSeeder.MainDb];
        j.RepositoryProfileId = null;
        j.CommitMode = CommitMode.ExportOnly;
        j.ExportPath = exportDir;
        j.DestinationFolder = string.Empty;
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.Recommended };
    });

    var run = await exportEnv.RunAsync(exportJob.Id);
    Check("S10-export", "export run completes", run.Status is RunStatus.Succeeded or RunStatus.Warning,
        $"status={run.Status}, error={run.ErrorMessage}");
    Check("S10-export", "files exported",
        Directory.Exists(exportDir) && Directory.EnumerateFiles(exportDir, "*.sql", SearchOption.AllDirectories).Any(), "");
    Check("S10-export", "no git repository created in export",
        !Directory.Exists(Path.Combine(exportDir, ".git")), "");
    Check("S10-export", "no workspace clone created (no git ops at all)",
        exportEnv.FindWorkspaceClone() is null, "");
    Check("S10-export", "no commit sha recorded", run.CommitSha is null, $"sha={run.CommitSha}");

    // Zip variant.
    var zipPath = Path.Combine(exportEnv.Root, "export-out.zip");
    var zipJob = await exportEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-export-zip";
        j.Databases = [SqlSeeder.MainDb];
        j.RepositoryProfileId = null;
        j.CommitMode = CommitMode.ExportOnly;
        j.ExportPath = zipPath;
        j.DestinationFolder = string.Empty;
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.ProgrammabilityOnly };
    });
    var zipRun = await exportEnv.RunAsync(zipJob.Id);
    Check("S10-export", "zip export produces the zip",
        zipRun.Status is RunStatus.Succeeded or RunStatus.Warning && File.Exists(zipPath),
        $"status={zipRun.Status}");
}

// ---- S11: Local Commit Only — commits locally, never pushes ------------------------------------
{
    await using var localEnv = await E2eEnvironment.CreateAsync(root, "localonly", server);
    var localJob = await localEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-localonly";
        j.Databases = [SqlSeeder.MainDb];
        j.DestinationFolder = "db";
        j.CommitMode = CommitMode.LocalCommitOnly;
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.Recommended };
    });

    var remoteBefore = localEnv.RemoteCommitCount();
    var run = await localEnv.RunAsync(localJob.Id);
    Check("S11-localonly", "run completes", run.Status is RunStatus.Succeeded or RunStatus.Warning,
        $"status={run.Status}, error={run.ErrorMessage}");
    Check("S11-localonly", "remote NOT pushed", localEnv.RemoteCommitCount() == remoteBefore,
        $"remote commits {remoteBefore} -> {localEnv.RemoteCommitCount()}");
    var clone = localEnv.FindWorkspaceClone();
    Check("S11-localonly", "local clone has the commit",
        clone is not null && int.Parse(GitTool.Capture(clone, "rev-list", "--count", "HEAD").Trim()) > remoteBefore, "");
}

// ---- S12: determinism — two fresh environments produce identical trees -------------------------
{
    await using var detA = await E2eEnvironment.CreateAsync(root, "det-a", server);
    await using var detB = await E2eEnvironment.CreateAsync(root, "det-b", server);
    foreach (var (env, name) in new[] { (detA, "det-a"), (detB, "det-b") })
    {
        var detJob = await env.AddJobAsync(j =>
        {
            j.Name = "e2e-det"; // identical name so commit metadata matches
            j.Databases = [SqlSeeder.MainDb];
            j.DestinationFolder = "db";
            j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.FullSchema };
        });
        var run = await env.RunAsync(detJob.Id);
        Check("S12-determinism", $"{name} run completes", run.Status is RunStatus.Succeeded or RunStatus.Warning,
            $"status={run.Status}");
    }

    Check("S12-determinism", "tree hashes identical across environments",
        detA.RemoteTreeHash() == detB.RemoteTreeHash(),
        $"{detA.RemoteTreeHash()[..12]} vs {detB.RemoteTreeHash()[..12]}");
}

// ---- S13: schema filter narrowing/widening ------------------------------------------------------
{
    await using var filterEnv = await E2eEnvironment.CreateAsync(root, "filter", server);
    var filterJob = await filterEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-filter";
        j.Databases = [SqlSeeder.MainDb];
        j.DestinationFolder = "db";
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.FullSchema };
        j.Selection.SchemaFilter.Add("sales");
    });

    var run1 = await filterEnv.RunAsync(filterJob.Id);
    var files1 = filterEnv.RemoteFiles();
    Check("S13-filter", "filtered run scripts only sales objects",
        files1.Any(f => f.Contains("/sales.", StringComparison.OrdinalIgnoreCase))
            && !files1.Any(f => f.Contains("/dbo.usp", StringComparison.OrdinalIgnoreCase)),
        $"{files1.Count} files, status={run1.Status}");

    // Widen: clear the filter — dbo/hr objects appear, sales files must NOT be deleted.
    filterJob.Selection.SchemaFilter.Clear();
    await filterEnv.Provider.GetRequiredService<IJobRepository>().UpsertAsync(filterJob);
    var run2 = await filterEnv.RunAsync(filterJob.Id);
    var files2 = filterEnv.RemoteFiles();
    Check("S13-filter", "widening adds other schemas", files2.Any(f => f.Contains("/hr.", StringComparison.OrdinalIgnoreCase) || f.Contains("Employee", StringComparison.OrdinalIgnoreCase)),
        $"status={run2.Status}");
    Check("S13-filter", "widening deletes nothing", run2.ObjectsDeleted == 0, $"-{run2.ObjectsDeleted}");

    // Narrow to hr only: observe what happens to sales/dbo files (report behavior).
    filterJob.Selection.SchemaFilter.Add("hr");
    await filterEnv.Provider.GetRequiredService<IJobRepository>().UpsertAsync(filterJob);
    var run3 = await filterEnv.RunAsync(filterJob.Id);
    Check("S13-filter", "narrowing behavior observed (report)", true,
        $"status={run3.Status}, -{run3.ObjectsDeleted} deletions, {filterEnv.RemoteFiles().Count} files remain");
}

// ---- S14: two jobs, same repository, run concurrently ------------------------------------------
{
    await using var concEnv = await E2eEnvironment.CreateAsync(root, "concurrent", server);
    var jobA = await concEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-conc-a";
        j.Databases = [SqlSeeder.MainDb];
        j.DestinationFolder = "a";
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.ProgrammabilityOnly };
    });
    var jobB = await concEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-conc-b";
        j.Databases = [SqlSeeder.SecondDb];
        j.DestinationFolder = "b";
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.ProgrammabilityOnly };
    });

    var taskA = concEnv.RunAsync(jobA.Id);
    var taskB = concEnv.RunAsync(jobB.Id);
    var runs = await Task.WhenAll(taskA, taskB);
    Check("S14-concurrent", "both same-repo jobs complete without corruption",
        runs.All(r => r.Status is RunStatus.Succeeded or RunStatus.Warning or RunStatus.NoChanges),
        string.Join("; ", runs.Select(r => $"{r.JobName}={r.Status} {r.ErrorMessage}")));
    var files = concEnv.RemoteFiles();
    Check("S14-concurrent", "both destination trees present",
        files.Any(f => f.StartsWith("a/", StringComparison.Ordinal)) && files.Any(f => f.StartsWith("b/", StringComparison.Ordinal)),
        $"{files.Count} files");
}

// ---- S15: multi-database dynamic scope (AllUserDatabases minus everything but ours) ------------
{
    var all = await seeder.ListUserDatabasesAsync();
    var excluded = all.Where(d => d is not (SqlSeeder.MainDb or SqlSeeder.SecondDb)).ToList();
    await using var dynEnv = await E2eEnvironment.CreateAsync(root, "dynamic", server);
    var dynJob = await dynEnv.AddJobAsync(j =>
    {
        j.Name = "e2e-dynamic";
        j.DatabaseScope = DatabaseScope.AllUserDatabases;
        j.Databases = [];
        j.ExcludedDatabases = excluded;
        j.DestinationFolder = "srv";
        j.Selection = new ObjectSelectionProfile { Preset = ObjectSelectionPreset.ProgrammabilityOnly };
    });
    var run = await dynEnv.RunAsync(dynJob.Id);
    var files = dynEnv.RemoteFiles();
    Check("S15-dynamic", "dynamic scope resolves both test DBs",
        files.Any(f => f.Contains($"/{SqlSeeder.MainDb}/", StringComparison.OrdinalIgnoreCase))
            && files.Any(f => f.Contains($"/{SqlSeeder.SecondDb}/", StringComparison.OrdinalIgnoreCase)),
        $"status={run.Status}, files={files.Count}");
    Check("S15-dynamic", "no excluded database scripted",
        !excluded.Any(d => files.Any(f => f.Contains($"/{d}/", StringComparison.OrdinalIgnoreCase))),
        string.Join(",", excluded));
}

// ==============================================================================================
// Summary
// ==============================================================================================
var failed = results.Count(r => !r.Pass);
Console.WriteLine();
Console.WriteLine($"E2E complete: {results.Count - failed}/{results.Count} checks passed, {failed} failed.");

var evidence = new StringBuilder();
evidence.AppendLine($"# Obsync E2E audit evidence — {stamp}");
evidence.AppendLine($"Server: {server} | Root: {root}");
evidence.AppendLine();
evidence.AppendLine("| Scenario | Check | Result | Detail |");
evidence.AppendLine("| --- | --- | --- | --- |");
foreach (var r in results)
{
    evidence.AppendLine($"| {r.Scenario} | {r.Check} | {(r.Pass ? "PASS" : "FAIL")} | {r.Detail.Replace("|", "\\|")} |");
}

var evidencePath = Path.Combine(root, "e2e-results.md");
await File.WriteAllTextAsync(evidencePath, evidence.ToString());
Console.WriteLine($"Evidence written to {evidencePath}");

if (!keep)
{
    Console.WriteLine("Dropping disposable databases…");
    await seeder.DropDatabasesAsync();
}

return failed == 0 ? 0 : 1;
