using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Obsync.Benchmark;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

// ---------------------------------------------------------------------------------------------
// Obsync benchmark harness.
//
// Drives the REAL production pipeline (metadata providers -> hashing -> file writes -> git
// commit) against a generated workload on a local SQL Server, with fully isolated state: a
// temporary obsync.db, a temporary workspaces root, and a local bare git repository as the
// "remote" (so git cost is measured without network noise and without touching GitHub).
//
//   dotnet run -c Release --project tools/Obsync.Benchmark -- --objects 10000
//
// Options:
//   --server <name>        SQL Server (default: localhost, Windows auth)
//   --database <name>      Benchmark database (default: ObsyncBench; created if missing)
//   --objects <n>          Total procs+views+functions to ensure (default 10000; 60/20/20 split)
//   --tables <n>           Tables to ensure — these exercise the SMO path (default 100)
//   --touch <n>            Procedures ALTERed before the incremental run (default 500)
//   --cancel-after <sec>   Also measure cancellation latency this many seconds into a fresh full run
//   --skip-generate        Reuse the existing workload without topping it up
//   --label <text>         Free-form label recorded in the report (e.g. "before-fix")
// ---------------------------------------------------------------------------------------------

var opt = BenchOptions.Parse(args);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var report = new StringBuilder();

Log($"Obsync benchmark — {opt.Objects:N0} module objects + {opt.Tables:N0} tables on {opt.Server}/{opt.Database}");

// ---- 1. Workload -----------------------------------------------------------------------------
var generator = new WorkloadGenerator(opt.Server, opt.Database);
if (!opt.SkipGenerate)
{
    var genWatch = Stopwatch.StartNew();
    var procs = (int)(opt.Objects * 0.6);
    var views = (int)(opt.Objects * 0.2);
    var functions = opt.Objects - procs - views;
    var workload = await generator.EnsureAsync(procs, views, functions, opt.Tables, includeHostileObjects: true);
    Log($"Workload ready in {genWatch.Elapsed.TotalSeconds:N1}s ({workload.NewlyCreated:N0} objects newly created).");
}

// ---- 2. Isolated engine environment ----------------------------------------------------------
var benchRoot = Path.Combine(Path.GetTempPath(), "obsync-bench", stamp);
Directory.CreateDirectory(benchRoot);
Log($"Bench root: {benchRoot}");

var env = await BenchEnvironment.CreateAsync(benchRoot, "main-suite", opt);

// ---- 3. Timed runs ---------------------------------------------------------------------------
var results = new List<RunResult>
{
    await env.RunTimedAsync("run1-full-initial"),
    await env.RunTimedAsync("run2-no-change"),
    await env.RunTimedAsync("run3-no-change-warm"),
};

Log($"Touching {opt.Touch:N0} procedures…");
await generator.TouchProceduresAsync(opt.Touch);
results.Add(await env.RunTimedAsync($"run4-incremental-{opt.Touch}-changed"));

// ---- 4. Cancellation latency (isolated state so the interrupted run can't pollute the suite) --
RunResult? cancelResult = null;
if (opt.CancelAfterSeconds > 0)
{
    Log($"Cancellation probe: cancelling {opt.CancelAfterSeconds}s into a fresh full run…");
    var cancelEnv = await BenchEnvironment.CreateAsync(benchRoot, "cancel-probe", opt);
    cancelResult = await cancelEnv.RunTimedAsync("cancel-probe-full", opt.CancelAfterSeconds);
    results.Add(cancelResult);
}

// ---- 5. Report -------------------------------------------------------------------------------
report.AppendLine($"# Obsync benchmark — {stamp}");
report.AppendLine();
report.AppendLine($"- Label: {opt.Label ?? "(none)"}");
report.AppendLine($"- Commit: {Git.Describe()}");
report.AppendLine($"- Machine: {Environment.MachineName}, {Environment.ProcessorCount} logical cores, " +
                  $"{Environment.OSVersion}");
report.AppendLine($"- SQL Server: {opt.Server} / {opt.Database}");
report.AppendLine($"- Workload: {opt.Objects:N0} module objects (60% procs / 20% views / 20% functions), " +
                  $"{opt.Tables:N0} tables (SMO path), 6 hostile-name/encrypted objects");
report.AppendLine($"- Mode: LocalCommitOnly against a local bare repository (full git cost, no network)");
report.AppendLine();
report.AppendLine(
    "| Run | Status | Scanned | +/~/- | Failed | Duration | Obj/s | Peak WS | Allocated | Progress events |");
report.AppendLine(
    "| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
foreach (var r in results)
{
    report.AppendLine(
        $"| {r.Name} | {r.Status} | {r.Scanned:N0} | +{r.Added:N0}/~{r.Modified:N0}/-{r.Deleted:N0} | {r.Failed:N0} " +
        $"| {r.Duration.TotalSeconds:N1}s | {r.ObjectsPerSecond:N0} | {r.PeakWorkingSetMb:N0} MB " +
        $"| {r.AllocatedMb:N0} MB | {r.ProgressEvents:N0} |");
}

report.AppendLine();
report.AppendLine("## Phase timings (seconds)");
report.AppendLine();
report.AppendLine("| Run | " + string.Join(" | ", Enum.GetNames<SyncPhase>()) + " |");
report.AppendLine("| --- |" + string.Concat(Enumerable.Repeat(" ---: |", Enum.GetNames<SyncPhase>().Length)));
foreach (var r in results)
{
    report.Append($"| {r.Name} |");
    foreach (var phase in Enum.GetValues<SyncPhase>())
    {
        report.Append(r.PhaseSeconds.TryGetValue(phase, out var s) ? $" {s:N1} |" : " — |");
    }

    report.AppendLine();
}

report.AppendLine();
var (files, bytes, gitBytes) = env.MeasureWorkspace();
report.AppendLine($"Workspace after suite: {files:N0} files, {bytes / 1024d / 1024:N1} MB working tree, " +
                  $"{gitBytes / 1024d / 1024:N1} MB .git");
if (cancelResult is not null)
{
    report.AppendLine($"Cancellation latency: {cancelResult.CancelLatency!.Value.TotalSeconds:N2}s from token " +
                      $"cancellation to run return (status {cancelResult.Status}).");
}

if (results.Any(r => r.ErrorMessage is not null))
{
    report.AppendLine();
    report.AppendLine("## Errors");
    foreach (var r in results.Where(r => r.ErrorMessage is not null))
    {
        report.AppendLine($"- {r.Name}: {r.ErrorMessage}");
    }
}

var outDir = Path.Combine(Git.RepoRoot(), "artifacts", "benchmarks");
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, $"bench-{opt.Objects}-{stamp}.md");
await File.WriteAllTextAsync(outPath, report.ToString());

Console.WriteLine();
Console.WriteLine(report.ToString());
Log($"Report written to {outPath}");
Log($"Temp state kept at {benchRoot} (delete it when done).");
return results.Any(r => r.Status == RunStatus.Failed) ? 1 : 0;

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

// ================================================================================================

internal sealed record BenchOptions(
    string Server, string Database, int Objects, int Tables, int Touch, int CancelAfterSeconds,
    bool SkipGenerate, string? Label)
{
    public static BenchOptions Parse(string[] args)
    {
        string server = "localhost", database = "ObsyncBench";
        int objects = 10_000, tables = 100, touch = 500, cancelAfter = 0;
        var skipGenerate = false;
        string? label = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": server = args[++i]; break;
                case "--database": database = args[++i]; break;
                case "--objects": objects = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--tables": tables = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--touch": touch = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cancel-after": cancelAfter = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--skip-generate": skipGenerate = true; break;
                case "--label": label = args[++i]; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        return new BenchOptions(server, database, objects, tables, touch, cancelAfter, skipGenerate, label);
    }
}

/// <summary>One fully isolated engine environment: temp state db, temp workspaces, local bare remote.</summary>
internal sealed class BenchEnvironment
{
    private readonly ServiceProvider _provider;
    private readonly Guid _jobId;
    private readonly string _workspacesRoot;

    private BenchEnvironment(ServiceProvider provider, Guid jobId, string workspacesRoot)
    {
        _provider = provider;
        _jobId = jobId;
        _workspacesRoot = workspacesRoot;
    }

    public static async Task<BenchEnvironment> CreateAsync(string benchRoot, string name, BenchOptions opt)
    {
        var root = Path.Combine(benchRoot, name);
        var workspaces = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspaces);

        // A local bare repository stands in for GitHub: full clone/commit cost, zero network.
        var remote = Git.CreateSeededBareRepository(Path.Combine(root, "remote.git"));

        var services = new ServiceCollection();
        services.AddLogging(); // no providers: engine logs are measured work, not benchmark output
        services.AddSingleton<ICredentialStore>(new StubCredentialStore());
        services.AddObsyncCore(Path.Combine(root, "state.db"), o => o.WorkspacesRoot = workspaces);
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile
        {
            Name = "bench",
            ServerName = opt.Server,
            TrustServerCertificate = true,
        };
        await provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);

        var repository = new GitRepositoryProfile
        {
            Name = "bench",
            Owner = "bench",
            RepositoryName = "bench",
            RemoteUrl = remote,
        };
        await provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repository);

        var job = new SyncJob
        {
            Name = $"bench-{name}",
            ConnectionProfileId = connection.Id,
            RepositoryProfileId = repository.Id,
            Databases = [opt.Database],
            Branch = "main",
            DestinationFolder = "db",
            CommitMode = CommitMode.LocalCommitOnly,
            Selection = new ObjectSelectionProfile
            {
                Preset = ObjectSelectionPreset.Custom,
                CustomTypes = [SqlObjectType.Schema, SqlObjectType.Table, SqlObjectType.View,
                               SqlObjectType.Function, SqlObjectType.StoredProcedure],
            },
        };
        await provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        return new BenchEnvironment(provider, job.Id, workspaces);
    }

    public async Task<RunResult> RunTimedAsync(string name, int cancelAfterSeconds = 0)
    {
        var engine = _provider.GetRequiredService<ISyncEngine>();

        var phaseStarts = new Dictionary<SyncPhase, TimeSpan>();
        var progressEvents = 0;
        var watch = new Stopwatch();
        var progress = new Progress<SyncProgress>(p =>
        {
            Interlocked.Increment(ref progressEvents);
            lock (phaseStarts)
            {
                phaseStarts.TryAdd(p.Phase, watch.Elapsed);
            }
        });

        using var cts = new CancellationTokenSource();
        long cancelRequestedAt = 0;
        cts.Token.Register(() => Interlocked.Exchange(ref cancelRequestedAt, Stopwatch.GetTimestamp()));

        // Clean memory baseline so per-run peaks are attributable to the run itself.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        long peakWorkingSet = 0;
        using var samplerCts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            var process = Process.GetCurrentProcess();
            while (!samplerCts.Token.IsCancellationRequested)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                try { await Task.Delay(200, samplerCts.Token); } catch (OperationCanceledException) { break; }
            }
        });

        if (cancelAfterSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(cancelAfterSeconds));
        }

        watch.Start();
        var run = await engine.RunJobAsync(_jobId, RunTrigger.Manual, progress, cts.Token);
        watch.Stop();
        samplerCts.Cancel();
        await sampler;

        var phaseDurations = ComputePhaseDurations(phaseStarts, watch.Elapsed);
        TimeSpan? cancelLatency = cancelRequestedAt > 0 ? Stopwatch.GetElapsedTime(cancelRequestedAt) : null;

        var result = new RunResult(
            name, run.Status, run.ObjectsScanned, run.ObjectsAdded, run.ObjectsModified, run.ObjectsDeleted,
            run.ObjectsFailed, watch.Elapsed,
            watch.Elapsed.TotalSeconds > 0 ? run.ObjectsScanned / watch.Elapsed.TotalSeconds : 0,
            peakWorkingSet / 1024d / 1024, (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1024d / 1024,
            progressEvents, phaseDurations, cancelLatency, run.ErrorMessage);

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}]   {name}: {run.Status}, scanned {run.ObjectsScanned:N0} " +
            $"(+{run.ObjectsAdded:N0}/~{run.ObjectsModified:N0}/-{run.ObjectsDeleted:N0}, {run.ObjectsFailed} failed) " +
            $"in {watch.Elapsed.TotalSeconds:N1}s, peak {result.PeakWorkingSetMb:N0} MB");
        return result;
    }

    public (int Files, long Bytes, long GitBytes) MeasureWorkspace()
    {
        int files = 0;
        long bytes = 0, gitBytes = 0;
        foreach (var file in Directory.EnumerateFiles(_workspacesRoot, "*", SearchOption.AllDirectories))
        {
            var length = new FileInfo(file).Length;
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                gitBytes += length;
            }
            else
            {
                files++;
                bytes += length;
            }
        }

        return (files, bytes, gitBytes);
    }

    private static Dictionary<SyncPhase, double> ComputePhaseDurations(
        Dictionary<SyncPhase, TimeSpan> starts, TimeSpan total)
    {
        // Phases are sequential; a phase lasts until the next reported phase starts.
        var ordered = starts.OrderBy(kv => kv.Value).ToList();
        var result = new Dictionary<SyncPhase, double>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var end = i + 1 < ordered.Count ? ordered[i + 1].Value : total;
            result[ordered[i].Key] = (end - ordered[i].Value).TotalSeconds;
        }

        return result;
    }
}

internal sealed record RunResult(
    string Name, RunStatus Status, int Scanned, int Added, int Modified, int Deleted, int Failed,
    TimeSpan Duration, double ObjectsPerSecond, double PeakWorkingSetMb, double AllocatedMb,
    int ProgressEvents, Dictionary<SyncPhase, double> PhaseSeconds, TimeSpan? CancelLatency, string? ErrorMessage);

/// <summary>The engine demands a token for git modes; a local bare remote never reads it.</summary>
internal sealed class StubCredentialStore : ICredentialStore
{
    public void Store(string key, string secret) { }
    public string? Retrieve(string key) => "bench-local-token";
    public void Delete(string key) { }
    public bool Exists(string key) => true;
}

internal static class Git
{
    public static string CreateSeededBareRepository(string barePath)
    {
        var seed = barePath + ".seed";
        Run(".", "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "benchmark seed\n");
        Run(seed, "add", ".");
        Run(seed, "-c", "user.name=bench", "-c", "user.email=bench@localhost", "commit", "-m", "seed");
        Run(".", "clone", "--bare", seed, barePath);
        // The seed working copy stays behind in the temp bench root — git object files are
        // read-only and everything under the bench root is throwaway anyway.
        return barePath;
    }

    public static string Describe() => RunCapture(RepoRoot(), "rev-parse", "--short", "HEAD").Trim();

    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Environment.CurrentDirectory;
    }

    private static void Run(string workingDirectory, params string[] args)
    {
        var output = RunCapture(workingDirectory, args);
        _ = output;
    }

    private static string RunCapture(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        return stdout;
    }
}
