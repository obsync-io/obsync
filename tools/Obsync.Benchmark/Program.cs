using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Obsync.Benchmark;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Results;

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
//   --object-bytes <n>     Characters per generated module body (default 4096 — real stored
//                          procedures run 2-10 KB; the old fixed body was ~750)
//   --reset-workload       Drop every generated object first, so the database ends up holding
//                          EXACTLY the requested counts at the requested body size. Without it,
//                          generation only TOPS UP: a request for 10,000 against a database that
//                          already holds 50,000 measures 50,000.
//   --mode <local|direct|pr>
//                          Delivery mode under test (default local):
//                            local  — commit to the clone, no push (excludes every push path and
//                                     the pull-request divergence sweep)
//                            direct — commit and push to the base branch on the local bare remote
//                            pr     — divergence sweep + head-branch recut + push; opening the
//                                     pull request is stubbed locally (no GitHub — see below)
//   --leave-proposals-open pr mode only: do NOT fast-forward the base branch onto each delivered
//                          proposal. Measures the closed-unmerged path, where the divergence sweep
//                          finds the tracked files absent and re-scans every type in full.
//   --touch <n>            Procedures ALTERed before the incremental run (default 500)
//   --cancel-after <sec>   Also measure cancellation latency this many seconds into a fresh full run
//   --skip-generate        Reuse the existing workload without topping it up
//   --label <text>         Free-form label recorded in the report (e.g. "before-fix")
//
// What pr mode does and does not measure: a pull request cannot be opened against a local bare
// repository, so the Octokit REST call is replaced by a local stand-in that records the request and
// returns a synthetic number. Everything before it is the real pipeline — the divergence sweep that
// reads and hashes every tracked file, the head-branch recut from the base, the reconcile, and the
// push. The REST call's own latency and failure modes are not measured.
// ---------------------------------------------------------------------------------------------

var opt = BenchOptions.Parse(args);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var report = new StringBuilder();

Log($"Obsync benchmark — requesting {opt.Objects:N0} module objects + {opt.Tables:N0} tables " +
    $"(~{opt.ObjectCharacters:N0} chars each) on {opt.Server}/{opt.Database}, mode {opt.Mode}");

// ---- 1. Workload -----------------------------------------------------------------------------
var generator = new WorkloadGenerator(opt.Server, opt.Database, opt.ObjectCharacters);
if (!opt.SkipGenerate)
{
    var genWatch = Stopwatch.StartNew();
    var procs = (int)(opt.Objects * 0.6);
    var views = (int)(opt.Objects * 0.2);
    var functions = opt.Objects - procs - views;
    var workload = await generator.EnsureAsync(
        procs, views, functions, opt.Tables, includeHostileObjects: true, reset: opt.ResetWorkload);
    Log($"Workload ready in {genWatch.Elapsed.TotalSeconds:N1}s " +
        $"({workload.NewlyCreated:N0} objects newly created, {workload.Dropped:N0} dropped).");
}

// What the database ACTUALLY holds. Top-up generation never removes anything, so the requested
// counts are a floor and nothing more — every figure the report prints as workload comes from here.
var inventory = await generator.MeasureAsync();
Log($"Workload measured: {inventory.TotalObjects:N0} objects " +
    $"({inventory.Procedures:N0} procs / {inventory.Views:N0} views / {inventory.Functions:N0} functions / " +
    $"{inventory.Tables:N0} tables / {inventory.Schemas:N0} schemas), " +
    $"average module definition {inventory.AverageModuleChars:N0} chars.");
if (!opt.SkipGenerate && inventory.TotalObjects > opt.Objects + opt.Tables + inventory.Schemas)
{
    Log("NOTE: the database holds more than was requested — a previous, larger run left objects " +
        "behind. The report records the measured counts. Use --reset-workload for an exact workload.");
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
report.AppendLine(
    $"- Workload REQUESTED: {opt.Objects:N0} module objects (60% procs / 20% views / 20% functions), " +
    $"{opt.Tables:N0} tables (SMO path), 6 hostile-name/encrypted objects, " +
    $"~{opt.ObjectCharacters:N0} characters per generated body " +
    (opt.SkipGenerate
        ? "(generation skipped — the request describes nothing about this run)"
        : opt.ResetWorkload
            ? "(--reset-workload: the workload was rebuilt to exactly this)"
            : "(top-up only: pre-existing objects were neither removed nor resized)"));
report.AppendLine(
    $"- Workload MEASURED in SQL before the suite: {inventory.TotalObjects:N0} objects — " +
    $"{inventory.Procedures:N0} procedures, {inventory.Views:N0} views, {inventory.Functions:N0} functions, " +
    $"{inventory.Tables:N0} tables, {inventory.Schemas:N0} schemas");
report.AppendLine(
    $"- Module bodies MEASURED: {inventory.Modules:N0} modules " +
    $"({inventory.ModulesWithoutDefinition:N0} with no readable definition, e.g. encrypted), " +
    $"average {inventory.AverageModuleChars:N0} characters, largest {inventory.MaxModuleChars:N0}, " +
    $"{inventory.TotalModuleChars / 1024d / 1024:N1} MB of definition text in total");
report.AppendLine(
    "- The Scanned column below is what the engine actually walked. It, and the measured counts " +
    "above, are the workload — the requested figures are not.");
report.AppendLine($"- Mode: {ModeDescription(opt.Mode)}");
if (opt.Mode == CommitMode.PullRequest)
{
    report.AppendLine(
        "- Pull request creation: **STUBBED** — no GitHub is contacted. The divergence sweep " +
        "(read + hash every tracked file), the head-branch recut from the base, the reconcile and " +
        "the push to the local bare remote are all the real pipeline; the Octokit REST call that " +
        "opens the pull request is replaced by a local stand-in that records the request and returns " +
        "a synthetic number, so run state advances exactly as it would after a real proposal. That " +
        "REST call's own latency and failure modes are NOT measured.");
    report.AppendLine(
        $"- Proposals recorded by the stub (main suite): {env.ProposalsRequested:N0}. " +
        (opt.LeaveProposalsOpen
            ? "Proposal merges were NOT simulated (--leave-proposals-open), so the base branch never " +
              "advanced: every later run's divergence sweep found the tracked files absent from the " +
              "recut tree and re-scanned those types in full — the closed-unmerged path."
            : $"Simulated merges: {env.SimulatedMerges:N0} — after each delivering run the base branch " +
              "in the bare remote was fast-forwarded onto the pushed head commit, standing in for a " +
              "reviewer merging the proposal."));
}

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
// Named for the MEASURED object count, never the requested one — the file name is the first thing
// anyone quotes, and `bench-10000-…` on a report that scanned 50,202 is how the mislabelling spread.
var outPath = Path.Combine(outDir, $"bench-{inventory.TotalObjects}-{stamp}.md");
await File.WriteAllTextAsync(outPath, report.ToString());

Console.WriteLine();
Console.WriteLine(report.ToString());
Log($"Report written to {outPath}");
Log($"Temp state kept at {benchRoot} (delete it when done).");
return results.Any(r => r.Status == RunStatus.Failed) ? 1 : 0;

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

static string ModeDescription(CommitMode mode) => mode switch
{
    CommitMode.LocalCommitOnly =>
        "LocalCommitOnly against a local bare repository — full commit cost, nothing pushed. This mode " +
        "exercises NEITHER the push paths NOR the pull-request divergence sweep (the sweep is gated on " +
        "CommitMode.PullRequest).",
    CommitMode.DirectCommit =>
        "DirectCommit against a local bare repository — commit and push to the base branch, no network. " +
        "The pull-request divergence sweep does not run in this mode.",
    CommitMode.PullRequest =>
        "PullRequest against a local bare repository — the divergence sweep runs, the head branch is " +
        "recut from the base every run, and it is pushed to the bare remote. No network.",
    _ => mode.ToString(),
};

// ================================================================================================

internal sealed record BenchOptions(
    string Server, string Database, int Objects, int Tables, int ObjectCharacters, bool ResetWorkload,
    CommitMode Mode, bool LeaveProposalsOpen, int Touch, int CancelAfterSeconds, bool SkipGenerate, string? Label)
{
    /// <summary>The branch a pull-request run targets, and the branch direct mode commits to.</summary>
    public const string BaseBranch = "main";

    public static BenchOptions Parse(string[] args)
    {
        string server = "localhost", database = "ObsyncBench";
        // 4,096 characters sits in the middle of the 2-10 KB a real stored procedure runs to. The
        // harness used to emit ~750-byte objects, which understates every working-tree, repository
        // and hashing figure derived from a run.
        int objects = 10_000, tables = 100, objectCharacters = 4_096, touch = 500, cancelAfter = 0;
        var mode = CommitMode.LocalCommitOnly;
        bool resetWorkload = false, leaveProposalsOpen = false, skipGenerate = false;
        string? label = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server": server = args[++i]; break;
                case "--database": database = args[++i]; break;
                case "--objects": objects = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--tables": tables = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--object-bytes": objectCharacters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--reset-workload": resetWorkload = true; break;
                case "--mode": mode = ParseMode(args[++i]); break;
                case "--leave-proposals-open": leaveProposalsOpen = true; break;
                case "--touch": touch = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cancel-after": cancelAfter = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--skip-generate": skipGenerate = true; break;
                case "--label": label = args[++i]; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (objectCharacters < 64)
        {
            throw new ArgumentException("--object-bytes must be at least 64.");
        }

        if (leaveProposalsOpen && mode != CommitMode.PullRequest)
        {
            throw new ArgumentException("--leave-proposals-open only applies to --mode pr.");
        }

        return new BenchOptions(
            server, database, objects, tables, objectCharacters, resetWorkload, mode, leaveProposalsOpen,
            touch, cancelAfter, skipGenerate, label);
    }

    private static CommitMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "local" or "localcommitonly" => CommitMode.LocalCommitOnly,
        "direct" or "directcommit" => CommitMode.DirectCommit,
        "pr" or "pullrequest" => CommitMode.PullRequest,
        _ => throw new ArgumentException($"Unknown --mode '{value}' (expected local, direct, or pr)."),
    };
}

/// <summary>One fully isolated engine environment: temp state db, temp workspaces, local bare remote.</summary>
internal sealed class BenchEnvironment
{
    private readonly ServiceProvider _provider;
    private readonly Guid _jobId;
    private readonly string _workspacesRoot;
    private readonly string _remotePath;
    private readonly BenchOptions _opt;
    private readonly LocalProposalRecorder _proposals;

    private BenchEnvironment(
        ServiceProvider provider, Guid jobId, string workspacesRoot, string remotePath, BenchOptions opt,
        LocalProposalRecorder proposals)
    {
        _provider = provider;
        _jobId = jobId;
        _workspacesRoot = workspacesRoot;
        _remotePath = remotePath;
        _opt = opt;
        _proposals = proposals;
    }

    /// <summary>Pull requests the local stand-in was asked to open (pr mode only).</summary>
    public int ProposalsRequested => _proposals.Requested;

    /// <summary>Delivered proposals fast-forwarded into the base branch, standing in for a reviewer merging.</summary>
    public int SimulatedMerges { get; private set; }

    public static async Task<BenchEnvironment> CreateAsync(string benchRoot, string name, BenchOptions opt)
    {
        var root = Path.Combine(benchRoot, name);
        var workspaces = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspaces);

        // A local bare repository stands in for GitHub: full clone/commit/push cost, zero network.
        var remote = Git.CreateSeededBareRepository(Path.Combine(root, "remote.git"));

        var services = new ServiceCollection();
        services.AddLogging(); // no providers: engine logs are measured work, not benchmark output
        services.AddSingleton<ICredentialStore>(new StubCredentialStore());

        // Registered BEFORE AddObsyncCore, which uses TryAdd — so this wins. A pull request cannot
        // be opened against a bare repository on disk, and the engine treats a failed PR call as an
        // undelivered run (state is not advanced), which would make every later run in the suite a
        // full re-scan and measure nothing anyone runs. The recorder lets delivery complete exactly
        // as a real proposal would; what it skips is one REST round trip, and the report says so.
        var proposals = new LocalProposalRecorder();
        services.AddSingleton<IGitHubService>(proposals);

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
            // In pull-request mode this is the BASE branch; the engine cuts its own head branch
            // from it every run. In the other git modes it is the branch that is committed to.
            Branch = BenchOptions.BaseBranch,
            DestinationFolder = "db",
            CommitMode = opt.Mode,
            Selection = new ObjectSelectionProfile
            {
                Preset = ObjectSelectionPreset.Custom,
                CustomTypes = [SqlObjectType.Schema, SqlObjectType.Table, SqlObjectType.View,
                               SqlObjectType.Function, SqlObjectType.StoredProcedure],
            },
        };
        await provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        return new BenchEnvironment(provider, job.Id, workspaces, remote, opt, proposals);
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

        MergeProposalIfRequested(run);

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

    /// <summary>
    /// Stands in for a reviewer merging the proposal: fast-forwards the base branch in the bare
    /// remote onto the commit the run just pushed to its head branch.
    /// </summary>
    /// <remarks>
    /// Without this the base branch never moves, so the next run recuts an empty head branch, the
    /// divergence sweep finds every tracked file missing and re-scans every type in full — for ever.
    /// That IS the real closed-unmerged behaviour and is worth measuring, which is what
    /// <c>--leave-proposals-open</c> selects; it is just not the steady state a healthy job is in,
    /// and measuring only that would misdescribe pull-request mode as badly as never running it.
    /// </remarks>
    private void MergeProposalIfRequested(SyncRun run)
    {
        if (_opt.Mode != CommitMode.PullRequest || _opt.LeaveProposalsOpen)
        {
            return;
        }

        // PullRequestNumber is set only once the proposal was recorded, which happens only after the
        // push succeeded — so the commit is provably in the bare remote and can be fast-forwarded to.
        if (run.PullRequestNumber is null || run.CommitSha is null)
        {
            return;
        }

        Git.SetBranch(_remotePath, BenchOptions.BaseBranch, run.CommitSha);
        SimulatedMerges++;
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

/// <summary>
/// Records the pull request a run wanted to open instead of opening one on GitHub — the only step
/// of pull-request mode that cannot run against a local bare repository.
/// </summary>
/// <remarks>
/// Everything the benchmark exists to measure happens before this: the divergence sweep that reads
/// and hashes every tracked file, the head-branch recut from the base, the head-branch reconcile,
/// and the push. What is skipped is one Octokit REST round trip — so a pull-request benchmark must
/// never be read as a measurement of GitHub's API latency or of how the engine handles its failures.
/// <para>
/// The other members are never reached by <c>ISyncEngine.RunJobAsync</c> (they serve the app's
/// wizard and diagnostics). They throw rather than return a plausible-looking answer, so if a
/// future engine change starts calling GitHub during a run the benchmark fails loudly instead of
/// quietly measuring a stub.
/// </para>
/// </remarks>
internal sealed class LocalProposalRecorder : IGitHubService
{
    private int _requested;

    public int Requested => _requested;

    public Task<Result<PullRequestInfo>> CreatePullRequestAsync(
        string token, string owner, string name, string title, string headBranch, string baseBranch,
        string body, IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default)
    {
        var number = Interlocked.Increment(ref _requested);
        return Task.FromResult(Result.Success(new PullRequestInfo(
            number,
            $"local://{owner}/{name}/pull/{number} (recorded locally: {headBranch} -> {baseBranch})",
            ReviewerWarning: null)));
    }

    public Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default) => throw Unexpected();

    public Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(
        string token, CancellationToken cancellationToken = default) => throw Unexpected();

    public Task<Result<IReadOnlyList<string>>> GetBranchesAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default) => throw Unexpected();

    public Task<Result<bool>> IsBranchProtectedAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default)
        => throw Unexpected();

    public Task<Result<IReadOnlyList<string>>> GetBranchRulesAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default)
        => throw Unexpected();

    private static InvalidOperationException Unexpected() => new(
        "The benchmark contacted GitHub. The harness only stands in for opening a pull request; any " +
        "other GitHub call during a run would be measured against a stub and must be handled here " +
        "deliberately.");
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

    /// <summary>Points a branch in a bare repository at an existing commit (a fast-forward "merge").</summary>
    public static void SetBranch(string barePath, string branch, string commitSha)
        => Run(barePath, "update-ref", $"refs/heads/{branch}", commitSha);

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
