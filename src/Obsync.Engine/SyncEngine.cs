using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Obsync.Data.Repositories;
using Obsync.Engine.Alerting;
using Obsync.Git;
using Obsync.GitHub;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Engine;

/// <summary>Runs a sync job end to end: script → hash → diff → write → commit → push → record.</summary>
public interface ISyncEngine
{
    Task<SyncRun> RunJobAsync(
        Guid jobId, RunTrigger trigger, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISyncEngine" />
public sealed class SyncEngine : ISyncEngine
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long a run waits for another job using the same repository before giving up.</summary>
    private static readonly TimeSpan WorkspaceLockTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Scripts larger than this are skipped: GitHub hard-rejects files over 100 MB and the failed
    /// push would wedge the branch. (Realistic source: reference-data exports with wide rows.)
    /// </summary>
    private const int MaxScriptChars = 95_000_000;

    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IRunRepository _runs;
    private readonly IObjectStateRepository _objectStates;
    private readonly IScriptingWatermarkRepository _watermarks;
    private readonly IAppSettingsRepository _appSettings;
    private readonly IReadOnlyList<IObjectScriptProvider> _scriptProviders;
    private readonly IServerObjectScriptProvider _serverProvider;
    private readonly ISqlServerProbe _probe;
    private readonly IDatabaseArtifactReader _artifactReader;
    private readonly IDatabaseDocumentationReader _documentationReader;
    private readonly ISecurityAnalysisReader _securityReader;
    private readonly IReferenceDataReader _referenceDataReader;
    private readonly IModifiedObjectReader _modifiedObjects;
    private readonly IGitWorkspace _gitWorkspace;
    private readonly IGitHubService _gitHub;
    private readonly IProxyProvider _proxy;
    private readonly ICredentialStore _credentialStore;
    private readonly IScriptNormalizer _normalizer;
    private readonly IObjectHasher _hasher;
    private readonly IObjectFilePathMapper _pathMapper;
    private readonly IRunAlertService _alerts;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ObsyncEngineOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IRunRepository runs,
        IObjectStateRepository objectStates,
        IScriptingWatermarkRepository watermarks,
        IAppSettingsRepository appSettings,
        IEnumerable<IObjectScriptProvider> scriptProviders,
        IServerObjectScriptProvider serverProvider,
        ISqlServerProbe probe,
        IDatabaseArtifactReader artifactReader,
        IDatabaseDocumentationReader documentationReader,
        ISecurityAnalysisReader securityReader,
        IReferenceDataReader referenceDataReader,
        IModifiedObjectReader modifiedObjects,
        IGitWorkspace gitWorkspace,
        IGitHubService gitHub,
        IProxyProvider proxy,
        ICredentialStore credentialStore,
        IScriptNormalizer normalizer,
        IObjectHasher hasher,
        IObjectFilePathMapper pathMapper,
        IRunAlertService alerts,
        IAuditWriter audit,
        IClock clock,
        IOptions<ObsyncEngineOptions> options,
        ILogger<SyncEngine> logger)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _runs = runs;
        _objectStates = objectStates;
        _watermarks = watermarks;
        _appSettings = appSettings;
        _scriptProviders = [.. scriptProviders];
        _serverProvider = serverProvider;
        _probe = probe;
        _artifactReader = artifactReader;
        _documentationReader = documentationReader;
        _securityReader = securityReader;
        _referenceDataReader = referenceDataReader;
        _modifiedObjects = modifiedObjects;
        _gitWorkspace = gitWorkspace;
        _gitHub = gitHub;
        _proxy = proxy;
        _credentialStore = credentialStore;
        _normalizer = normalizer;
        _hasher = hasher;
        _pathMapper = pathMapper;
        _alerts = alerts;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncRun> RunJobAsync(
        Guid jobId, RunTrigger trigger, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId} was not found.");

        // Cross-process run lock: the app, the scheduler service, and the CLI all execute jobs
        // against the same database and git workspace, so the same job must never run twice at
        // once across processes. Held until this method returns (after final persistence).
        using var runLock = JobRunLock.TryAcquire(ObsyncPaths.LocksRoot, job.Id);
        if (runLock is null)
        {
            if (trigger == RunTrigger.Manual)
            {
                throw new InvalidOperationException(
                    $"\"{job.Name}\" is already running in another Obsync process (the scheduler service or CLI). " +
                    "Wait for that run to finish.");
            }

            // A scheduled occurrence came due while a previous run of this job is still active in
            // another process. Policy: skip this occurrence (running it would collide on the job's
            // git workspace); the next occurrence fires normally. Log-only, like window skips.
            _logger.LogWarning(
                "Job {JobId} ({JobName}) {Trigger} run skipped — a previous run is still active in another process.",
                job.Id, job.Name, trigger);
            return new SyncRun
            {
                JobId = job.Id,
                JobName = job.Name,
                Trigger = trigger,
                Status = RunStatus.NoChanges,
                StartedAt = _clock.UtcNow,
                CompletedAt = _clock.UtcNow,
                Tags = [.. job.Tags],
            };
        }

        // Maintenance window: a SCHEDULED (or catch-up) run outside the allowed window is skipped
        // (manual "Run Now" and startup runs bypass). Skips are log-only — advance the cached
        // next-run so the UI stays accurate, and return an un-persisted run (the scheduler ignores
        // the result).
        if (trigger is RunTrigger.Scheduled or RunTrigger.CatchUp
            && !job.Schedule.IsWithinMaintenanceWindow(_clock.UtcNow.ToLocalTime()))
        {
            _logger.LogInformation("Job {JobId} ({JobName}) skipped — outside its maintenance window.", job.Id, job.Name);
            await _jobs.UpdateNextRunAtAsync(job.Id, job.Schedule.GetNextRun(_clock.UtcNow), cancellationToken).ConfigureAwait(false);
            return new SyncRun
            {
                JobId = job.Id,
                JobName = job.Name,
                Trigger = trigger,
                Status = RunStatus.NoChanges,
                StartedAt = _clock.UtcNow,
                CompletedAt = _clock.UtcNow,
                Tags = [.. job.Tags],
            };
        }

        var connection = await _connections.GetAsync(job.ConnectionProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job's SQL connection profile was not found.");

        // Export Only has no GitHub repository or token; the git modes load both.
        GitRepositoryProfile? repository = null;
        string? gitToken = null;
        if (job.CommitMode != CommitMode.ExportOnly)
        {
            if (job.RepositoryProfileId is not { } repositoryId)
            {
                throw new InvalidOperationException("The job has no destination repository.");
            }

            repository = await _repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The job's GitHub repository profile was not found.");
            gitToken = _credentialStore.Retrieve(CredentialKeys.GitHubToken(repository.Id));
        }

        var sqlPassword = connection.RequiresPassword
            ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(connection.Id))
            : null;

        // Different jobs can share one repository profile — and therefore one git workspace. A
        // second lock (per repository) serializes them: interleaved checkout/add in a shared clone
        // would sweep another job's half-written tree into the wrong branch/commit. Waiting
        // (bounded) rather than skipping, so overlapping schedules run back-to-back instead of one
        // silently dropping its sync.
        IDisposable? workspaceLock = null;
        if (job.CommitMode != CommitMode.ExportOnly && job.RepositoryProfileId is { } workspaceRepoId)
        {
            var lockName = $"repo-{workspaceRepoId:N}";
            workspaceLock = JobRunLock.TryAcquire(ObsyncPaths.LocksRoot, lockName);
            if (workspaceLock is null)
            {
                progress?.Report(new SyncProgress(SyncPhase.PreparingRepository,
                    "Waiting for another job that uses the same repository to finish…"));
                _logger.LogInformation(
                    "Job {JobId} ({JobName}) is waiting for the repository workspace lock.", job.Id, job.Name);
                workspaceLock = await JobRunLock.WaitAsync(
                    ObsyncPaths.LocksRoot, lockName, WorkspaceLockTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (workspaceLock is null)
            {
                if (trigger == RunTrigger.Manual)
                {
                    throw new InvalidOperationException(
                        $"Another job kept the repository busy for over {WorkspaceLockTimeout.TotalMinutes:N0} minutes — " +
                        "wait for it to finish, then run again.");
                }

                _logger.LogWarning(
                    "Job {JobId} ({JobName}) {Trigger} run skipped — another job kept the shared repository busy " +
                    "for over {Timeout} minutes.", job.Id, job.Name, trigger, WorkspaceLockTimeout.TotalMinutes);
                return new SyncRun
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Trigger = trigger,
                    Status = RunStatus.NoChanges,
                    StartedAt = _clock.UtcNow,
                    CompletedAt = _clock.UtcNow,
                    Tags = [.. job.Tags],
                };
            }
        }

        using var heldWorkspaceLock = workspaceLock;

        var started = _clock.UtcNow;
        var run = new SyncRun
        {
            RunKey = started.LocalDateTime.ToString("yyyyMMdd-HHmmss"),
            JobId = job.Id,
            JobName = job.Name,
            Trigger = trigger,
            TriggeredBy = CurrentActor.Name,
            Status = RunStatus.Running,
            ServerName = connection.ServerName,
            // The dynamic scope is resolved against the live server inside ExecuteAsync; until then
            // the run row shows the scope description rather than a stale list.
            Databases = job.DatabaseScope == DatabaseScope.AllUserDatabases
                ? "All user databases"
                : string.Join(", ", job.Databases),
            StartedAt = started,
            Tags = [.. job.Tags],
        };
        await _runs.InsertAsync(run, cancellationToken).ConfigureAwait(false);

        var context = new RunContext(job, connection, repository, sqlPassword, gitToken, progress);
        try
        {
            await ExecuteAsync(run, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            context.Log(SyncLogLevel.Warning, "Run cancelled.");
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation mid-query surfaces as a provider error, not OperationCanceledException
            // (SqlClient throws "a severe error occurred on the current command"). The user asked
            // to stop — record Cancelled, not a scary failure.
            run.Status = RunStatus.Cancelled;
            context.Log(SyncLogLevel.Warning, "Run cancelled.", ex.Message);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            context.Log(SyncLogLevel.Error, "Run failed.", ex.ToString());
            _logger.LogError(ex, "Sync run {RunKey} for job {JobId} failed.", run.RunKey, job.Id);
        }
        finally
        {
            // Persist the final state with a NON-cancelled token: when a run is cancelled the run
            // token is already tripped, and reusing it here would throw and leave the run stuck on
            // "Running" with no logs. These writes are quick and must always complete.
            var persistToken = CancellationToken.None;

            var completed = _clock.UtcNow;
            run.CompletedAt = completed;
            run.DurationMs = (long)(completed - started).TotalMilliseconds;
            run.ObjectsAdded = context.Added;
            run.ObjectsModified = context.Modified;
            run.ObjectsDeleted = context.Deleted;
            run.ObjectsScanned = context.Scanned;
            run.ObjectsFailed = context.Failed;

            foreach (var log in context.Logs)
            {
                log.RunId = run.Id;
            }

            await _runs.UpdateAsync(run, persistToken).ConfigureAwait(false);
            await _runs.AddLogsAsync(context.Logs, persistToken).ConfigureAwait(false);
            await _runs.AddChangesAsync(run.Id, context.Changes, persistToken).ConfigureAwait(false);
            await _jobs.UpdateRunSummaryAsync(job.Id, new JobRunSummary
            {
                LastRunId = run.Id,
                LastStatus = run.Status,
                LastRunAt = run.StartedAt,
                LastChangeCount = run.ChangeCount,
                LastCommitSha = run.CommitSha,
                // Standard cadences compute a preview here; the scheduler refines cron next-runs.
                NextRunAt = job.Schedule.GetNextRun(completed) ?? job.RunSummary.NextRunAt,
            }, persistToken).ConfigureAwait(false);

            // One audit event per run outcome, written here so scheduled/service runs are covered
            // too (they never pass through the app's coordinator). The actor is the account that
            // executed the run — the interactive user for Run Now, the service account otherwise;
            // the detail carries the trigger, change count, commit, and error so the trail answers
            // "what ran, what changed, and did the push succeed" on its own.
            await _audit.WriteAsync(
                run.Status == RunStatus.Failed ? AuditAction.RunFailed : AuditAction.RunCompleted,
                "Job", job.Id.ToString(), job.Name,
                $"{run.Trigger} run {run.RunKey} {OutcomeSummary(run)}", persistToken).ConfigureAwait(false);
        }

        // Best-effort alerting (email/webhook) after the run is fully persisted, with a
        // non-cancelled token for the same reason as above. A send failure is logged and
        // swallowed — an alert never fails or delays the run beyond the sender's short timeout.
        try
        {
            await _alerts.NotifyAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert delivery for run {RunKey} failed.", run.RunKey);
        }

        return run;
    }

    // The human-readable outcome for the run's audit event.
    private static string OutcomeSummary(SyncRun run)
    {
        var summary = run.Status switch
        {
            RunStatus.Succeeded => $"succeeded — {run.ChangeCount} changes",
            RunStatus.NoChanges => "completed — no changes",
            RunStatus.Warning => $"completed with warnings — {run.ChangeCount} changes, {run.ObjectsFailed} objects failed",
            RunStatus.Cancelled => "was cancelled",
            RunStatus.Failed => $"failed — {run.ErrorMessage}",
            _ => run.Status.ToString(),
        };
        return run.CommitSha is { Length: > 0 } sha
            ? $"{summary}, commit {sha[..Math.Min(7, sha.Length)]}"
            : summary;
    }

    private async Task ExecuteAsync(SyncRun run, RunContext context, CancellationToken cancellationToken)
    {

        // Fail fast with an actionable message when a required secret is missing. The usual cause is
        // that the Windows service runs under a different account than the app that saved the
        // credentials (Windows Credential Manager vaults are per-user), so scheduled runs can't read
        // them even though "Run Now" from the app works.
        if (context.Connection.RequiresPassword && string.IsNullOrEmpty(context.SqlPassword))
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage =
                "The SQL password for this server was not found in the credential store. If the Obsync " +
                "service runs under a different Windows account than the app used to save it, configure " +
                "the service to run under that same account, or re-save the server credentials.";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return;
        }

        // Resolve the concrete database list before either delivery path. The dynamic scope queries
        // the server fresh on every run, so databases created after the job was saved are picked up
        // automatically. Returns null after marking the run Failed with an actionable message.
        var databases = await ResolveDatabasesAsync(run, context, cancellationToken).ConfigureAwait(false);
        if (databases is null)
        {
            return;
        }

        context.Databases = databases;

        // Export Only scripts straight to a folder/zip — no git clone, commit, push, or token.
        if (context.Job.CommitMode == CommitMode.ExportOnly)
        {
            await ExecuteExportAsync(run, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(context.GitToken))
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage =
                "The GitHub access token for this repository was not found in the credential store. If the " +
                "Obsync service runs under a different Windows account than the app used to save it, configure " +
                "the service to run under that same account, or re-save the repository token.";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return;
        }

        context.Report(SyncPhase.Connecting, $"Connecting to {context.Connection.ServerName}…");

        var baseBranch = string.IsNullOrWhiteSpace(context.Job.Branch) ? context.Repository!.DefaultBranch : context.Job.Branch!;
        var isPullRequest = context.Job.CommitMode == CommitMode.PullRequest;
        // Pull request mode commits to a fresh per-run head branch cut from the base; direct mode
        // commits straight to the base branch.
        var headBranch = isPullRequest ? HeadBranchName(context.Job.Name, run.RunKey) : baseBranch;
        var localPath = Path.Combine(
            await ResolveWorkspacesRootAsync(cancellationToken).ConfigureAwait(false),
            context.Repository!.Id.ToString("N"));
        var proxyUrl = (await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false))?.GitProxyUrl;
        // Committer identity is configurable in Settings so git blame reflects the owning team.
        var committer = await _appSettings.GetCommitterAsync(cancellationToken).ConfigureAwait(false);
        var gitContext = new GitWorkspaceContext
        {
            RemoteUrl = context.Repository.EffectiveRemoteUrl,
            Branch = headBranch,
            BaseBranch = isPullRequest ? baseBranch : null,
            LocalPath = localPath,
            AuthorizationHeader = context.GitToken is null ? null : GitHubService.BuildAuthorizationHeader(context.GitToken),
            CommitterName = string.IsNullOrWhiteSpace(committer.Name) ? CommitterIdentity.Default.Name : committer.Name.Trim(),
            CommitterEmail = string.IsNullOrWhiteSpace(committer.Email) ? _options.CommitterEmail : committer.Email!.Trim(),
            NetworkRetryCount = context.Job.Advanced.GitRetryCount,
            ProxyUrl = proxyUrl,
        };

        context.Report(SyncPhase.PreparingRepository, "Preparing the GitHub workspace…");
        var prepared = await _gitWorkspace.PrepareAsync(gitContext, cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = prepared.Error;
            context.Log(SyncLogLevel.Error, "Could not prepare the GitHub workspace.", prepared.Error);
            return;
        }

        await ScriptServerAsync(run, context, localPath, cancellationToken).ConfigureAwait(false);

        var types = context.Job.Selection.ResolveTypes();
        foreach (var database in context.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScriptDatabaseAsync(run, context, database, types, localPath, cancellationToken).ConfigureAwait(false);
        }

        await FinalizeAsync(run, context, gitContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where repository clones live: the Settings override when set, else the built-in default.
    /// Resolved per run so a settings change applies without restarting the app or the service
    /// (changed roots make the next run clone fresh; the old location is left untouched).
    /// </summary>
    private async Task<string> ResolveWorkspacesRootAsync(CancellationToken cancellationToken)
    {
        var overridePath = await _appSettings.GetWorkspacesRootOverrideAsync(cancellationToken).ConfigureAwait(false);
        var root = string.IsNullOrWhiteSpace(overridePath) ? _options.WorkspacesRoot : overridePath.Trim();
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Resolves the databases this run will script. A fixed list passes through unchanged; the
    /// dynamic scope enumerates the server's online user databases minus the exclusion list.
    /// Returns null (with the run marked Failed) when the server can't be enumerated or nothing matches.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveDatabasesAsync(
        SyncRun run, RunContext context, CancellationToken cancellationToken)
    {
        if (context.Job.DatabaseScope != DatabaseScope.AllUserDatabases)
        {
            return context.Job.Databases;
        }

        context.Report(SyncPhase.Connecting, "Resolving user databases…");
        var result = await _probe.GetDatabasesAsync(context.Connection, context.SqlPassword, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = $"Could not enumerate the server's databases: {result.Error}";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return null;
        }

        var databases = FilterUserDatabases(result.Value, context.Job.ExcludedDatabases, out var offline);
        foreach (var name in offline)
        {
            context.Log(SyncLogLevel.Warning, $"Skipping database '{name}' — it is not online.");
        }

        if (databases.Count == 0)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = "No online user databases matched the job's scope. " +
                "Check the exclusion list, or verify the server has user databases.";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return null;
        }

        run.Databases = string.Join(", ", databases);
        context.Log(SyncLogLevel.Info, $"Resolved {databases.Count} user database(s): {run.Databases}.");
        return databases;
    }

    // Internal for tests: the pure scope-filtering rule (online, not excluded, name-ordered).
    internal static List<string> FilterUserDatabases(
        IReadOnlyList<SqlDatabaseInfo> found, IReadOnlyCollection<string> excludedDatabases, out List<string> skippedOffline)
    {
        var excluded = new HashSet<string>(excludedDatabases, StringComparer.OrdinalIgnoreCase);
        skippedOffline = [.. found.Where(d => !d.IsOnline && !excluded.Contains(d.Name)).Select(d => d.Name)];
        return [.. found
            .Where(d => d.IsOnline && !excluded.Contains(d.Name))
            .Select(d => d.Name)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    // Export Only: script a full snapshot straight to a folder or .zip — no git, no GitHub, no state.
    private async Task ExecuteExportAsync(SyncRun run, RunContext context, CancellationToken cancellationToken)
    {
        var destination = context.Job.ExportPath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = "Export Only requires an export destination (a folder or a .zip path).";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return;
        }

        var isZip = destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var outputRoot = isZip
            ? Path.Combine(Path.GetTempPath(), $"obsync-export-{run.Id:N}")
            : destination;

        context.Report(SyncPhase.Connecting, $"Connecting to {context.Connection.ServerName}…");
        Directory.CreateDirectory(outputRoot);

        await ScriptServerAsync(run, context, outputRoot, cancellationToken, fullSnapshot: true).ConfigureAwait(false);

        var types = context.Job.Selection.ResolveTypes();
        foreach (var database in context.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScriptDatabaseAsync(run, context, database, types, outputRoot, cancellationToken, fullSnapshot: true)
                .ConfigureAwait(false);
        }

        if (isZip)
        {
            context.Report(SyncPhase.Committing, "Packaging the export…");
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            ZipFile.CreateFromDirectory(outputRoot, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
            try
            {
                Directory.Delete(outputRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp staging directory.
            }
        }

        run.ObjectsScanned = context.Scanned;
        run.ObjectsAdded = context.Added;
        run.ObjectsFailed = context.Failed;
        run.Status = context.Scanned == 0 ? RunStatus.NoChanges : RunStatus.Succeeded;
        EscalateForSkips(run, context);
        context.Log(SyncLogLevel.Info,
            run.Status == RunStatus.NoChanges
                ? "No objects to export."
                : $"Exported {context.Added:N0} object(s) to {destination}.");
        context.Report(SyncPhase.Completed, "Run completed.");
    }

    private async Task ScriptDatabaseAsync(
        SyncRun run, RunContext context, string database, IReadOnlyList<SqlObjectType> types,
        string localPath, CancellationToken cancellationToken, bool fullSnapshot = false)
    {
        // The dynamic scope always nests a per-database folder — the resolved set can grow over
        // time, and flipping a single-database layout to nested later would move every file. A
        // fixed list keeps the existing rule: nest only when more than one database is selected.
        var dbFolder = context.Job.DatabaseScope == DatabaseScope.AllUserDatabases || context.Databases.Count > 1
            ? RepositoryLayout.Combine(context.Job.DestinationFolder, database)
            : context.Job.DestinationFolder;

        // Export Only writes a full snapshot: an empty prior map makes every object "Added" (so all
        // are written), the deletion pass a no-op, and no object_states are consulted.
        var prior = fullSnapshot
            ? new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase)
            : BuildPriorMap(
                await _objectStates.GetForJobDatabaseAsync(context.Job.Id, database, cancellationToken).ConfigureAwait(false),
                database);

        // Accumulators are written from many worker threads concurrently, so they are all thread-safe.
        // `prior` is only read during processing, so a plain dictionary is safe to share.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var changedStates = new ConcurrentBag<TrackedObjectState>();
        var inventory = new ConcurrentBag<ObjectInventoryEntry>();
        var skipped = new ConcurrentBag<string>();
        var ignoreRules = await LoadIgnoreRulesAsync(context, localPath, dbFolder, cancellationToken).ConfigureAwait(false);

        // Snapshot the run counters so the docs artifact can tell whether THIS database changed
        // (databases are processed sequentially, so the delta is unambiguous).
        var changesBeforeDatabase = context.Added + context.Modified;

        // Diff + write + record + track for a single scripted item (a SQL object or a synthetic
        // database artifact). Artifacts ride the same change-detection path so an options- or
        // permissions-only change still produces a commit, while an unchanged run still produces none.
        // Runs concurrently across workers for objects, then sequentially for the trailing artifacts.
        async Task ApplyItemAsync(ScriptedObjectIdentity identity, string rawScript, string relativePath, CancellationToken ct)
        {
            // Synthetic items (manifest artifacts, reference data) skip the object inventory, the
            // scanned counter, and the ignore rules — they are engine-generated from explicit
            // configuration, not discovered schema objects.
            var isArtifact = identity.Type is SqlObjectType.DatabaseArtifact or SqlObjectType.ReferenceData;
            var key = StateKey(identity);
            seen.TryAdd(key, 0);

            // Honor the ignore rules (.obsyncignore + the job's patterns): don't script or inventory an
            // ignored object. It is marked "seen" above, so the deletion pass leaves whatever is already
            // in the repo untouched.
            if (!isArtifact && ignoreRules.Matches(identity.Type, identity.Schema, identity.Name))
            {
                return;
            }

            var script = context.Job.Selection.NormalizeScripts ? _normalizer.Normalize(rawScript) : rawScript;
            var hash = _hasher.ComputeHash(script);
            var repoRelativePath = RepositoryLayout.Combine(dbFolder, relativePath);
            var absolutePath = Path.Combine(localPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!isArtifact)
            {
                context.IncrementScanned();
                inventory.Add(new ObjectInventoryEntry(
                    identity.Type.ToString(), identity.Schema, identity.Name, relativePath, hash));

                // A live count during the longest phase — throttled so a VLDB run posts a few
                // updates per second, not one per object.
                var scanned = context.Scanned;
                if (scanned % 500 == 0)
                {
                    context.Report(SyncPhase.Scripting, $"Scripting {database}… {scanned:N0} objects processed", scanned);
                }
            }

            if (script.Length > MaxScriptChars)
            {
                // Skip-and-report (file retained via "seen"): committing it would make GitHub
                // reject the push and wedge the branch for every later run.
                context.IncrementFailed();
                skipped.Add($"{identity.Type} {DescribeIdentity(identity)} — the generated script is " +
                    $"{script.Length / (1024 * 1024):N0} MB; GitHub rejects files over 100 MB");
                return;
            }

            var hasPrior = prior.TryGetValue(key, out var priorState);

            // Layout changes move an object's file (e.g. a job growing from one database to several
            // nests per-database folders). Remove the old path so the repository does not keep a
            // stale duplicate tree; the state row follows to the new path with this run's upsert.
            if (hasPrior && !string.Equals(priorState!.FilePath, repoRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                var oldAbsolute = Path.Combine(localPath, priorState.FilePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(oldAbsolute))
                {
                    File.Delete(oldAbsolute);
                }
            }

            var changeType = !hasPrior ? ChangeType.Added : priorState!.LastHash == hash ? ChangeType.Unchanged : ChangeType.Modified;
            if (changeType == ChangeType.Unchanged)
            {
                // Self-heal drift: our recorded hash matches, but if the file is missing from the
                // working tree (manual delete, fresh clone with stale state, a prior reset) rewrite it
                // so the repository reflects the true source. If it is present, there is nothing to do.
                if (File.Exists(absolutePath))
                {
                    return;
                }

                changeType = ChangeType.Modified;
            }

            await WriteFileAsync(context, localPath, repoRelativePath, script, context.Job.LocalExportPath, dbFolder, relativePath, ct)
                .ConfigureAwait(false);

            if (changeType == ChangeType.Added)
            {
                context.IncrementAdded();
            }
            else
            {
                context.IncrementModified();
            }

            context.AddChange(new ObjectChange
            {
                ChangeType = changeType,
                ObjectType = identity.Type,
                Schema = identity.Schema,
                Name = identity.Name,
                RelativePath = repoRelativePath,
                PreviousHash = hasPrior ? priorState!.LastHash : null,
                NewHash = hash,
            });

            changedStates.Add(new TrackedObjectState
            {
                JobId = context.Job.Id,
                DatabaseName = database,
                ObjectType = identity.Type,
                SchemaName = identity.Schema,
                ObjectName = identity.Name,
                ObjectId = identity.ObjectId,
                FilePath = repoRelativePath,
                LastHash = hash,
                LastScriptedAt = _clock.UtcNow,
                LastRunId = run.Id,
                LastStatus = RunStatus.Running,
            });
        }

        // An object a provider could not script (encrypted/CLR module, SMO failure) is recorded as a
        // skip rather than silently dropped: it is counted, marked "seen" (so it is not deleted), and
        // surfaced as a warning. Runs concurrently, so it only touches thread-safe accumulators.
        // A skip of an object WITH prior state (a transient failure on something previously scripted)
        // also blocks that type's watermark from advancing this run — otherwise a change that
        // predates the new watermark would never be re-examined. Skips of objects with NO prior
        // state (e.g. permanently unscriptable encrypted modules) are covered by the planner's
        // no-prior violation rule instead, which forces a full scan of the type every run — gating
        // the watermark for those too would just keep it stale forever with no added safety.
        var skippedTypes = new ConcurrentDictionary<SqlObjectType, byte>();
        Task RecordSkipAsync(RawScriptedObject raw)
        {
            var key = StateKey(raw.Identity);
            seen.TryAdd(key, 0);
            if (prior.ContainsKey(key))
            {
                skippedTypes.TryAdd(raw.Identity.Type, 0);
            }

            context.IncrementScanned();
            context.IncrementFailed();
            skipped.Add($"{raw.Identity.Type} {DescribeIdentity(raw.Identity)} — {raw.SkipReason}");
            return Task.CompletedTask;
        }

        context.Report(SyncPhase.Scripting, $"Scripting objects in {database}…");

        var workers = context.Job.Advanced.MaxParallelWorkers > 0
            ? context.Job.Advanced.MaxParallelWorkers
            : Environment.ProcessorCount;

        // Incremental scripting: snapshot modify_dates FIRST, mark unchanged objects as seen with
        // their prior hashes, and hand the providers per-type watermark filters. Export runs are
        // always full snapshots, and a first run (no prior state) has nothing to skip.
        var incrementalWatermarks = !fullSnapshot && context.Job.Advanced.IncrementalScripting && prior.Count > 0
            ? await PlanIncrementalAsync(context, database, types, prior, seen, inventory, ignoreRules, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // A single producer streams from the providers (one SqlDataReader at a time) into a bounded
        // channel; the worker pool normalizes, hashes, diffs, and writes objects in parallel.
        await ChannelPipeline.RunAsync(
            StreamProvidersAsync(context, database, types, workers, incrementalWatermarks, cancellationToken),
            (raw, ct) => raw.SkipReason is not null
                ? RecordSkipAsync(raw)
                : ApplyItemAsync(raw.Identity, raw.Script, _pathMapper.MapRelativePath(raw.Identity), ct),
            workers,
            cancellationToken).ConfigureAwait(false);

        // A reference table that cannot be scripted this run is reported like a scripting skip:
        // counted as failed and marked "seen" so its committed file is never deleted by a blip.
        void RecordDataSkip(ScriptedObjectIdentity identity, string reason)
        {
            seen.TryAdd(StateKey(identity), 0);
            context.IncrementFailed();
            skipped.Add($"Reference data {DescribeIdentity(identity)} — {reason}");
        }

        // A generated file whose catalog read failed is a skip too — the objects are already
        // scripted at this point, and failing the whole run over a permissions/options query would
        // discard that work. Marked "seen" so the committed copy is never deleted on a blip.
        void RecordArtifactSkip(ScriptedObjectIdentity identity, string reason)
        {
            seen.TryAdd(StateKey(identity), 0);
            context.IncrementFailed();
            skipped.Add($"Generated file {identity.Name} — {reason}");
        }

        // Artifacts, reference data, documentation, and deletions run after the parallel phase, so
        // they touch the accumulators alone.
        var inventorySnapshot = inventory.ToList();
        await GenerateDatabaseArtifactsAsync(context, database, inventorySnapshot, ApplyItemAsync, RecordArtifactSkip, cancellationToken).ConfigureAwait(false);
        await GenerateReferenceDataAsync(context, database, ApplyItemAsync, RecordDataSkip, cancellationToken).ConfigureAwait(false);
        await GenerateDocumentationAsync(
            context, database, inventorySnapshot, prior, seen, changesBeforeDatabase, ApplyItemAsync, cancellationToken)
            .ConfigureAwait(false);

        if (!skipped.IsEmpty)
        {
            var details = skipped.ToList();
            context.Log(SyncLogLevel.Warning,
                $"{details.Count:N0} item(s) in {database} could not be scripted and were skipped.",
                string.Join("\n", details.Take(100)));
        }

        ApplyDeletions(context, database, localPath, prior, seen);

        // A type that had skips must not advance its watermark: the skipped object's change may
        // predate the new watermark, and advancing past it would hide the change from every later
        // incremental run.
        if (!skippedTypes.IsEmpty && context.PendingWatermarks.TryGetValue(database, out var staged))
        {
            context.PendingWatermarks[database] = staged
                .Where(pair => !skippedTypes.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        context.PendingStates.AddRange(changedStates);
    }

    /// <summary>
    /// The incremental-scripting snapshot pass for one database: reads every capable object's
    /// <c>modify_date</c> in one bulk query, runs the pure <see cref="IncrementalPlanner"/>, marks
    /// each planned skip as seen/scanned with its prior hash in the inventory (no state write, no
    /// file write, no change record), and queues the new watermarks for persistence with the final
    /// run status. Returns the provider filter — only the safe ("filterable") types' watermarks —
    /// or null when nothing can be filtered.
    /// </summary>
    private async Task<IReadOnlyDictionary<SqlObjectType, DateTime>?> PlanIncrementalAsync(
        RunContext context, string database, IReadOnlyList<SqlObjectType> types,
        Dictionary<string, TrackedObjectState> prior,
        ConcurrentDictionary<string, byte> seen, ConcurrentBag<ObjectInventoryEntry> inventory,
        IgnoreRules ignoreRules, CancellationToken cancellationToken)
    {
        var capableTypes = types.Where(IncrementalPlanner.CapableTypes.Contains).ToList();
        if (capableTypes.Count == 0)
        {
            return null;
        }

        var watermarks = await _watermarks.GetForJobDatabaseAsync(context.Job.Id, database, cancellationToken).ConfigureAwait(false);
        var snapshot = await _modifiedObjects.GetSnapshotAsync(
            context.Connection, context.SqlPassword, database, capableTypes,
            context.Job.Advanced.SqlCommandTimeoutSeconds, context.Job.Advanced.SqlLockTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        // "Ignored" for planning purposes means either an ignore-rule match or an out-of-filter
        // schema — the providers never yield those, so they must neither be skipped as unchanged
        // nor treated as safety violations. Removing the schema filter later un-ignores them, and
        // the resulting no-prior violation forces the full scan that brings them into scope.
        var schemaFilter = context.Job.Selection.SchemaFilter;
        var plan = IncrementalPlanner.Plan(snapshot, prior, watermarks,
            (type, schema, name) => ignoreRules.Matches(type, schema, name)
                || (schemaFilter.Count > 0 && !schemaFilter.Contains(schema)));

        foreach (var skip in plan.SkippedItems)
        {
            var identity = new ScriptedObjectIdentity(skip.Item.Type, skip.Item.Schema, skip.Item.Name);
            seen.TryAdd(StateKey(identity), 0);
            context.IncrementScanned();
            // The inventory wants the database-root-relative path; the mapper is deterministic, so
            // this reproduces exactly what scripting the object would have recorded.
            inventory.Add(new ObjectInventoryEntry(
                skip.Item.Type.ToString(), skip.Item.Schema, skip.Item.Name,
                _pathMapper.MapRelativePath(identity), skip.PriorState.LastHash));
        }

        // Ignored objects: seen (committed files retained, matching the full-scan path) but never
        // scanned or inventoried — full scans exclude them from both as well.
        foreach (var item in plan.IgnoredItems)
        {
            seen.TryAdd(StateKey(new ScriptedObjectIdentity(item.Type, item.Schema, item.Name)), 0);
        }

        if (plan.NewWatermarks.Count > 0)
        {
            context.PendingWatermarks[database] = plan.NewWatermarks;
        }

        if (plan.SkippedItems.Count > 0)
        {
            context.Log(SyncLogLevel.Info,
                $"Incremental: skipped {plan.SkippedItems.Count:N0} unchanged object(s) in {database}.");
        }

        if (plan.FilterableTypes.Count == 0)
        {
            return null;
        }

        return watermarks
            .Where(pair => plan.FilterableTypes.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Scripts the job's selected server-level (instance-scoped) objects — logins, server roles,
    /// credentials, linked servers, Agent jobs/operators/alerts — plus the server-configuration
    /// artifact, through the same hash → diff → write path as database objects. Files land under
    /// the destination folder's <c>server/</c> tree (the catalog folder names carry the prefix);
    /// prior state and deletions are scoped by the <see cref="RepositoryLayout.ServerScopeName"/>
    /// sentinel. No-op when the job selects no server types.
    /// </summary>
    private async Task ScriptServerAsync(
        SyncRun run, RunContext context, string localPath, CancellationToken cancellationToken, bool fullSnapshot = false)
    {
        var serverTypes = context.Job.Selection.ResolveServerTypes();
        if (serverTypes.Count == 0)
        {
            return;
        }

        // Server files sit directly under the destination folder — never inside a per-database folder.
        var serverRoot = context.Job.DestinationFolder;

        var prior = fullSnapshot
            ? new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase)
            : BuildPriorMap(
                await _objectStates.GetForJobDatabaseAsync(context.Job.Id, RepositoryLayout.ServerScopeName, cancellationToken).ConfigureAwait(false),
                "The server scope");

        // Accumulators are written from many worker threads concurrently, so they are all thread-safe.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var changedStates = new ConcurrentBag<TrackedObjectState>();
        var skipped = new ConcurrentBag<string>();
        var failedTypes = new ConcurrentDictionary<SqlObjectType, byte>();
        // Both the root .obsyncignore and a server/.obsyncignore apply to the server pass.
        var ignoreRules = await LoadIgnoreRulesAsync(
            context, localPath, RepositoryLayout.Combine(serverRoot, RepositoryLayout.ServerFolder), cancellationToken).ConfigureAwait(false);

        // The ScriptDatabaseAsync apply path, minus the object inventory (server objects have none).
        async Task ApplyItemAsync(ScriptedObjectIdentity identity, string rawScript, string relativePath, CancellationToken ct)
        {
            var isArtifact = identity.Type is SqlObjectType.DatabaseArtifact;
            var key = StateKey(identity);
            seen.TryAdd(key, 0);

            if (!isArtifact && ignoreRules.Matches(identity.Type, identity.Schema, identity.Name))
            {
                return;
            }

            var script = context.Job.Selection.NormalizeScripts ? _normalizer.Normalize(rawScript) : rawScript;
            var hash = _hasher.ComputeHash(script);
            var repoRelativePath = RepositoryLayout.Combine(serverRoot, relativePath);
            var absolutePath = Path.Combine(localPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!isArtifact)
            {
                context.IncrementScanned();
            }

            var hasPrior = prior.TryGetValue(key, out var priorState);
            var changeType = !hasPrior ? ChangeType.Added : priorState!.LastHash == hash ? ChangeType.Unchanged : ChangeType.Modified;
            if (changeType == ChangeType.Unchanged)
            {
                // Self-heal drift exactly like the database pass: rewrite a missing file.
                if (File.Exists(absolutePath))
                {
                    return;
                }

                changeType = ChangeType.Modified;
            }

            await WriteFileAsync(context, localPath, repoRelativePath, script, context.Job.LocalExportPath, serverRoot, relativePath, ct)
                .ConfigureAwait(false);

            if (changeType == ChangeType.Added)
            {
                context.IncrementAdded();
            }
            else
            {
                context.IncrementModified();
            }

            context.AddChange(new ObjectChange
            {
                ChangeType = changeType,
                ObjectType = identity.Type,
                Schema = identity.Schema,
                Name = identity.Name,
                RelativePath = repoRelativePath,
                PreviousHash = hasPrior ? priorState!.LastHash : null,
                NewHash = hash,
            });

            changedStates.Add(new TrackedObjectState
            {
                JobId = context.Job.Id,
                DatabaseName = RepositoryLayout.ServerScopeName,
                ObjectType = identity.Type,
                SchemaName = identity.Schema,
                ObjectName = identity.Name,
                ObjectId = identity.ObjectId,
                FilePath = repoRelativePath,
                LastHash = hash,
                LastScriptedAt = _clock.UtcNow,
                LastRunId = run.Id,
                LastStatus = RunStatus.Running,
            });
        }

        // A server object the provider could not script is recorded as a skip, never silently
        // dropped. The skip's type is also remembered so the deletion pass below leaves every prior
        // file of that type alone — an Agent permission blip must not delete committed job scripts.
        Task RecordSkipAsync(RawScriptedObject raw)
        {
            seen.TryAdd(StateKey(raw.Identity), 0);
            failedTypes.TryAdd(raw.Identity.Type, 0);
            context.IncrementScanned();
            context.IncrementFailed();
            skipped.Add($"{raw.Identity.Type} {DescribeIdentity(raw.Identity)} — {raw.SkipReason}");
            return Task.CompletedTask;
        }

        context.Report(SyncPhase.Scripting, "Scripting server-level objects…");

        var workers = context.Job.Advanced.MaxParallelWorkers > 0
            ? context.Job.Advanced.MaxParallelWorkers
            : Environment.ProcessorCount;

        var request = new ScriptRequest
        {
            Profile = context.Connection,
            Password = context.SqlPassword,
            Database = string.Empty, // the server scope has no database
            Types = serverTypes,
            Selection = context.Job.Selection,
            CommandTimeoutSeconds = context.Job.Advanced.SqlCommandTimeoutSeconds,
            SqlLockTimeoutSeconds = context.Job.Advanced.SqlLockTimeoutSeconds,
            MaxRetries = context.Job.Advanced.SqlRetryCount,
        };

        await ChannelPipeline.RunAsync(
            _serverProvider.ScriptAsync(request, cancellationToken),
            (raw, ct) => raw.SkipReason is not null
                ? RecordSkipAsync(raw)
                : ApplyItemAsync(raw.Identity, raw.Script, _pathMapper.MapRelativePath(raw.Identity), ct),
            workers,
            cancellationToken).ConfigureAwait(false);

        // The server-configuration artifact always rides with the server pass: one cheap bulk query,
        // and drifted sp_configure values are exactly what instance versioning exists to catch.
        var configuration = await _artifactReader.ReadServerConfigurationAsync(
            context.Connection, context.SqlPassword,
            context.Job.Advanced.SqlCommandTimeoutSeconds, context.Job.Advanced.SqlLockTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        await ApplyItemAsync(
            ArtifactIdentity("server-configuration"), configuration, RepositoryLayout.ServerConfigurationFile, cancellationToken)
            .ConfigureAwait(false);

        // The server security review rides the server pass the same way: sysadmin membership, the
        // sa login, password policy, and high-risk server grants, versioned so drift is a commit.
        if (context.Job.Selection.IncludeSecurityReview)
        {
            var findings = await _securityReader.ReadServerFindingsAsync(
                context.Connection, context.SqlPassword,
                context.Job.Advanced.SqlCommandTimeoutSeconds, context.Job.Advanced.SqlLockTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);
            var review = SecurityReviewWriter.Build($"server {context.Connection.ServerName}", findings);
            await ApplyItemAsync(
                ArtifactIdentity("server-security-review"), review, RepositoryLayout.ServerSecurityReviewFile, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!skipped.IsEmpty)
        {
            var details = skipped.ToList();
            context.Log(SyncLogLevel.Warning,
                $"{details.Count:N0} server-level item(s) could not be scripted and were skipped.",
                string.Join("\n", details.Take(100)));
        }

        // Suspend deletions for any type that had a scripting failure this run (see RecordSkipAsync).
        foreach (var (key, state) in prior)
        {
            if (failedTypes.ContainsKey(state.ObjectType))
            {
                seen.TryAdd(key, 0);
            }
        }

        ApplyDeletions(context, RepositoryLayout.ServerScopeName, localPath, prior, seen);

        context.PendingStates.AddRange(changedStates);
    }

    /// <summary>
    /// Streams every requested object across providers in sequence — metadata fast-path first, then
    /// SMO — yielding one <see cref="RawScriptedObject"/> at a time. A single consumer (the pipeline
    /// producer) advances this, so the providers' SqlDataReaders are never touched concurrently.
    /// </summary>
    private async IAsyncEnumerable<RawScriptedObject> StreamProvidersAsync(
        RunContext context, string database, IReadOnlyList<SqlObjectType> types, int workers,
        IReadOnlyDictionary<SqlObjectType, DateTime>? incrementalWatermarks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var provider in _scriptProviders)
        {
            var providerTypes = types.Where(t => SqlObjectTypeCatalog.Get(t).Strategy == provider.Strategy).ToList();
            if (providerTypes.Count == 0)
            {
                continue;
            }

            var request = new ScriptRequest
            {
                Profile = context.Connection,
                Password = context.SqlPassword,
                Database = database,
                Types = providerTypes,
                Selection = context.Job.Selection,
                CommandTimeoutSeconds = context.Job.Advanced.SqlCommandTimeoutSeconds,
                SqlLockTimeoutSeconds = context.Job.Advanced.SqlLockTimeoutSeconds,
                MaxRetries = context.Job.Advanced.SqlRetryCount,
                IncrementalWatermarks = incrementalWatermarks,
                // Capped at 8 so a wide worker pool cannot hammer the source server with
                // scripting connections; SMO uses this to partition large table collections.
                ScriptingParallelism = Math.Min(8, workers),
            };

            await foreach (var raw in provider.ScriptAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return raw;
            }
        }
    }

    /// <summary>
    /// Generates the per-database manifest files (object inventory, database options, permissions)
    /// and feeds each through the same change-detection path as a scripted object.
    /// </summary>
    private async Task GenerateDatabaseArtifactsAsync(
        RunContext context, string database, IReadOnlyList<ObjectInventoryEntry> inventory,
        Func<ScriptedObjectIdentity, string, string, CancellationToken, Task> apply,
        Action<ScriptedObjectIdentity, string> recordSkip, CancellationToken cancellationToken)
    {
        var selection = context.Job.Selection;
        var timeout = context.Job.Advanced.SqlCommandTimeoutSeconds;
        var lockTimeout = context.Job.Advanced.SqlLockTimeoutSeconds;

        // A failed catalog read becomes a reported skip (like documentation and reference data),
        // never a run failure — every object is already scripted by the time these generate.
        async Task GenerateAsync(string name, string repositoryFile, Func<Task<string>> read)
        {
            var identity = ArtifactIdentity(name);
            try
            {
                var content = await read().ConfigureAwait(false);
                await apply(identity, content, repositoryFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                recordSkip(identity, ex.Message);
            }
        }

        if (selection.IncludeObjectInventory)
        {
            await GenerateAsync("object-inventory", RepositoryLayout.ObjectInventoryFile,
                () => Task.FromResult(ObjectInventoryWriter.Serialize(context.Connection.ServerName, database, inventory)))
                .ConfigureAwait(false);
        }

        if (selection.IncludeDatabaseOptions)
        {
            await GenerateAsync("database-options", RepositoryLayout.DatabaseOptionsFile,
                () => _artifactReader.ReadDatabaseOptionsAsync(
                    context.Connection, context.SqlPassword, database, timeout, lockTimeout, cancellationToken))
                .ConfigureAwait(false);
        }

        if (selection.IncludeDatabasePermissionsFile)
        {
            await GenerateAsync("permissions", RepositoryLayout.PermissionsFile,
                () => _artifactReader.ReadPermissionsAsync(
                    context.Connection, context.SqlPassword, database, timeout, lockTimeout, cancellationToken))
                .ConfigureAwait(false);
        }

        // Like the permissions file, the review is read every run: grants and role membership do
        // not bump any modify_date, so posture drift is only caught by re-reading the catalog.
        if (selection.IncludeSecurityReview)
        {
            await GenerateAsync("security-review", RepositoryLayout.SecurityReviewFile, async () =>
            {
                var findings = await _securityReader.ReadDatabaseFindingsAsync(
                    context.Connection, context.SqlPassword, database, timeout, lockTimeout, cancellationToken)
                    .ConfigureAwait(false);
                return SecurityReviewWriter.Build(database, findings);
            }).ConfigureAwait(false);
        }
    }

    /// <summary>The data dictionary documents at most this many tables (alphabetical); the docs file notes the rest.</summary>
    private const int MaxDocumentedTables = 2000;

    /// <summary>
    /// Generates <c>docs/README.md</c> (object index + data dictionary) through the shared
    /// change-detection path. The dictionary read is a full catalog query, so it only runs when
    /// this database actually changed this run (or the docs were never generated) — an unchanged
    /// incremental run stays as fast as before. When generation is skipped or fails, the tracked
    /// file is marked seen so the deletion pass never removes committed docs on a blip.
    /// </summary>
    private async Task GenerateDocumentationAsync(
        RunContext context, string database, IReadOnlyList<ObjectInventoryEntry> inventory,
        Dictionary<string, TrackedObjectState> prior, ConcurrentDictionary<string, byte> seen,
        int changesBeforeDatabase,
        Func<ScriptedObjectIdentity, string, string, CancellationToken, Task> apply, CancellationToken cancellationToken)
    {
        if (!context.Job.Selection.IncludeDocumentation)
        {
            // Not marked seen: turning the option off lets the deletion pass clean the file up,
            // exactly like the other database artifacts.
            return;
        }

        var identity = ArtifactIdentity("documentation");
        var key = StateKey(identity);

        // "Changed" means: objects were added/modified in this database, something tracked was not
        // seen again (a pending deletion), or the docs were never generated. Extended-property-only
        // edits do not bump modify_date, so a description-only change surfaces with the next real
        // schema change — the same caveat incremental scripting already documents.
        var hasPendingDeletion = prior.Keys.Any(k =>
            !string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !seen.ContainsKey(k));
        var changed = context.Added + context.Modified > changesBeforeDatabase
            || hasPendingDeletion
            || !prior.ContainsKey(key);
        if (!changed)
        {
            seen.TryAdd(key, 0);
            return;
        }

        try
        {
            var data = await _documentationReader.ReadAsync(
                context.Connection, context.SqlPassword, database, MaxDocumentedTables,
                context.Job.Advanced.SqlCommandTimeoutSeconds, context.Job.Advanced.SqlLockTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);
            var markdown = DatabaseDocumentationWriter.Build(context.Connection.ServerName, database, inventory, data);
            await apply(identity, markdown, RepositoryLayout.DocumentationFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            seen.TryAdd(key, 0);
            context.IncrementFailed();
            context.Log(SyncLogLevel.Warning, $"Could not generate documentation for {database}.", ex.Message);
        }
    }

    /// <summary>
    /// Scripts each configured reference table's data through the shared change-detection path.
    /// Per-table problems (missing table, over the cap, SQL errors) become reported skips — one
    /// bad entry never sinks the run.
    /// </summary>
    private async Task GenerateReferenceDataAsync(
        RunContext context, string database,
        Func<ScriptedObjectIdentity, string, string, CancellationToken, Task> apply,
        Action<ScriptedObjectIdentity, string> recordSkip, CancellationToken cancellationToken)
    {
        var tables = context.Job.Selection.ReferenceDataTables;
        if (tables.Count == 0)
        {
            return;
        }

        context.Report(SyncPhase.Scripting, $"Scripting reference data in {database}…");
        foreach (var entry in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (schema, table) = SplitTableName(entry);
            var identity = new ScriptedObjectIdentity(SqlObjectType.ReferenceData, schema, table);
            try
            {
                var result = await _referenceDataReader.ReadTableDataAsync(
                    context.Connection, context.SqlPassword, database, schema, table,
                    context.Job.Advanced.ReferenceDataMaxRows,
                    context.Job.Advanced.SqlCommandTimeoutSeconds,
                    context.Job.Advanced.SqlLockTimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);

                if (result.SkipReason is not null)
                {
                    recordSkip(identity, result.SkipReason);
                    continue;
                }

                await apply(identity, result.Script!, RepositoryLayout.ReferenceDataFile(schema, table), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                recordSkip(identity, ex.Message);
            }
        }
    }

    // Internal for tests: "schema.table" → parts; a bare table name defaults to dbo.
    internal static (string Schema, string Table) SplitTableName(string entry)
    {
        var trimmed = entry.Trim();
        var dot = trimmed.IndexOf('.');
        return dot < 0
            ? ("dbo", trimmed)
            : (trimmed[..dot].Trim(), trimmed[(dot + 1)..].Trim());
    }

    private static ScriptedObjectIdentity ArtifactIdentity(string name) =>
        new(SqlObjectType.DatabaseArtifact, string.Empty, name);

    private static void ApplyDeletions(
        RunContext context, string database, string localPath,
        Dictionary<string, TrackedObjectState> prior, ConcurrentDictionary<string, byte> seen)
    {
        if (!context.Job.Selection.RemoveDroppedObjects)
        {
            return;
        }

        var deletedIds = new List<long>();
        foreach (var (key, state) in prior)
        {
            if (seen.ContainsKey(key))
            {
                continue;
            }

            var absolute = Path.Combine(localPath, state.FilePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
            }

            context.IncrementDeleted();
            context.AddChange(new ObjectChange
            {
                ChangeType = ChangeType.Deleted,
                ObjectType = state.ObjectType,
                Schema = state.SchemaName,
                Name = state.ObjectName,
                RelativePath = state.FilePath,
                PreviousHash = state.LastHash,
            });

            deletedIds.Add(state.Id);
        }

        // The state rows are removed by PersistStatesAsync only after the changeset is delivered —
        // deleting them here would forget the deletion forever if the commit/push later failed.
        context.PendingDeletedStateIds.AddRange(deletedIds);
    }

    private async Task FinalizeAsync(SyncRun run, RunContext context, GitWorkspaceContext gitContext, CancellationToken cancellationToken)
    {
        context.Report(SyncPhase.DetectingChanges,
            $"Detected {context.Added} added, {context.Modified} modified, {context.Deleted} deleted.");
        context.Log(SyncLogLevel.Info, $"Scanned {context.Scanned:N0} objects. " +
            $"{context.Added} added, {context.Modified} modified, {context.Deleted} deleted" +
            (context.Failed > 0 ? $", {context.Failed} skipped." : "."));

        run.ObjectsAdded = context.Added;
        run.ObjectsModified = context.Modified;
        run.ObjectsDeleted = context.Deleted;
        run.ObjectsScanned = context.Scanned;
        run.ObjectsFailed = context.Failed;

        var hasChanges = context.Changes.Count > 0;

        if (!hasChanges)
        {
            var isPullRequest = gitContext.BaseBranch is not null;
            var isLocalOnly = context.Job.CommitMode == CommitMode.LocalCommitOnly;

            // No object changes this run. In DIRECT mode two things can still need doing:
            //  1) A prior run may have committed but failed to push — re-push it now (never lose work).
            //  2) If RunOnlyIfChanges is off, record a "heartbeat" empty commit that a sync ran.
            // In PR mode the head branch is fresh each run, so no changes simply means no PR.
            if (!isPullRequest && !context.Job.Schedule.RunOnlyIfChanges)
            {
                await CommitAndPushAsync(run, context, gitContext, allowEmpty: true, cancellationToken).ConfigureAwait(false);
            }
            else if (!isPullRequest && !isLocalOnly && await _gitWorkspace.HasUnpushedCommitsAsync(gitContext, cancellationToken).ConfigureAwait(false))
            {
                context.Log(SyncLogLevel.Info, "No new changes, but a previous commit was never pushed — pushing it now.");
                await PushAsync(run, context, gitContext, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                run.Status = RunStatus.NoChanges;
                context.Log(SyncLogLevel.Info, "No changes detected — nothing to commit.");
            }

            // Nothing was pending this run (no changes), so there is no state to protect — persist
            // unconditionally to keep watermarks advancing on healthy no-change runs.
            await PersistStatesAsync(context, run.Status, run.CommitSha, delivered: true, cancellationToken).ConfigureAwait(false);
            EscalateForSkips(run, context);
            context.Report(SyncPhase.Completed, "Run completed.");
            return;
        }

        await CommitAndPushAsync(run, context, gitContext, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        await PersistStatesAsync(context, run.Status, run.CommitSha, context.ChangesDelivered, cancellationToken).ConfigureAwait(false);
        EscalateForSkips(run, context);
        context.Report(SyncPhase.Completed, "Run completed.");
    }

    private async Task CommitAndPushAsync(
        SyncRun run, RunContext context, GitWorkspaceContext gitContext, bool allowEmpty, CancellationToken cancellationToken)
    {
        context.Report(SyncPhase.Committing, "Creating commit…");
        var (subject, body) = CommitMessageBuilder.Build(run, context.Job, context.Changes);
        var commit = await _gitWorkspace.CommitAllAsync(gitContext, subject, body, allowEmpty, cancellationToken).ConfigureAwait(false);

        if (!commit.Success)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = commit.Error;
            context.Log(SyncLogLevel.Error, "Commit failed.", commit.Error);
            return;
        }

        var isLocalOnly = context.Job.CommitMode == CommitMode.LocalCommitOnly;

        if (commit.CommitSha is not null)
        {
            run.CommitSha = commit.CommitSha;
            // Local Commit Only never pushes, so a github.com commit URL would 404 — omit it.
            if (!isLocalOnly)
            {
                run.CommitUrl = GitHubService.BuildCommitUrl(context.Repository!.Owner, context.Repository.RepositoryName, commit.CommitSha);
            }

            context.Log(SyncLogLevel.Info, $"Created commit {commit.CommitSha[..Math.Min(7, commit.CommitSha.Length)]}.");
        }

        // Nothing new to do (no new commit, and — for the push modes — no stranded commit either).
        if (!commit.HadChanges
            && (isLocalOnly || !await _gitWorkspace.HasUnpushedCommitsAsync(gitContext, cancellationToken).ConfigureAwait(false)))
        {
            // The detected changes produced an identical git tree (e.g. a file already carried the
            // new content) — the repository already reflects them, so state may advance.
            context.ChangesDelivered = true;
            run.Status = RunStatus.NoChanges;
            return;
        }

        // Local Commit Only stops here: the commit lives in the local clone, to be pushed later.
        if (isLocalOnly)
        {
            context.ChangesDelivered = true;
            run.Status = RunStatus.Succeeded;
            context.Log(SyncLogLevel.Info, "Committed locally (not pushed).");
            return;
        }

        // Direct mode: the local commit is durable delivery — even if the push below fails, the
        // stranded commit is preserved and re-pushed by the next run. PR mode is different: its
        // head branch is recut from the base every run, so delivery is only the opened PR.
        if (gitContext.BaseBranch is null)
        {
            context.ChangesDelivered = true;
        }

        await PushAsync(run, context, gitContext, cancellationToken).ConfigureAwait(false);

        // Pull request mode: the head branch is now pushed — open the PR against the base branch.
        if (gitContext.BaseBranch is not null && run.Status == RunStatus.Succeeded)
        {
            await OpenPullRequestAsync(run, context, gitContext, subject, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OpenPullRequestAsync(
        SyncRun run, RunContext context, GitWorkspaceContext gitContext, string title, string body, CancellationToken cancellationToken)
    {
        context.Report(SyncPhase.Pushing, "Opening the pull request…");
        var result = await _gitHub.CreatePullRequestAsync(
            context.GitToken!, context.Repository!.Owner, context.Repository.RepositoryName,
            title, gitContext.Branch, gitContext.BaseBranch!, body, context.Job.Reviewers, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            // The head branch pushed but the PR did not open — a partial success the user must act on.
            run.Status = RunStatus.Warning;
            run.ErrorMessage = result.Error;
            context.Log(SyncLogLevel.Warning, $"The branch pushed, but opening the pull request failed. {result.Error}");
            return;
        }

        var pr = result.Value;
        context.ChangesDelivered = true;
        run.PullRequestUrl = pr.HtmlUrl;
        run.PullRequestNumber = pr.Number;
        context.Log(SyncLogLevel.Info, $"Opened pull request #{pr.Number}. {pr.HtmlUrl}");
        if (pr.ReviewerWarning is not null)
        {
            context.Log(SyncLogLevel.Warning, pr.ReviewerWarning);
        }
    }

    /// <summary>
    /// The per-run head branch name for pull-request mode, e.g. <c>obsync/salesdb-sync/20260702-230000</c>.
    /// Deterministic given the job name and run key.
    /// </summary>
    public static string HeadBranchName(string jobName, string runKey)
    {
        var slug = Slugify(jobName);
        return $"obsync/{(slug.Length == 0 ? "job" : slug)}/{runKey}";
    }

    // Lowercase, collapse non-alphanumeric runs to single dashes, trim dashes — a ref-safe slug.
    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private async Task PushAsync(SyncRun run, RunContext context, GitWorkspaceContext gitContext, CancellationToken cancellationToken)
    {
        context.Report(SyncPhase.Pushing, "Pushing to GitHub…");
        var push = await _gitWorkspace.PushAsync(gitContext, cancellationToken).ConfigureAwait(false);
        if (push.IsFailure)
        {
            run.Status = RunStatus.Warning;
            // The banner/toast show ErrorMessage — give them the explained, actionable reason;
            // the raw git stderr stays available in the log entry's technical details.
            var reason = ExplainPushFailure(push.Error);
            run.ErrorMessage = $"The commit was created locally but the push to GitHub failed. {reason}";
            context.Log(SyncLogLevel.Warning, run.ErrorMessage, push.Error);
        }
        else
        {
            run.Status = RunStatus.Succeeded;
            context.Log(SyncLogLevel.Info, "Pushed to GitHub.");
        }
    }

    // Turns raw git push stderr into a short, actionable reason for the user-facing log line.
    private static string ExplainPushFailure(string? error)
    {
        var text = (error ?? string.Empty).ToLowerInvariant();
        if (text.Contains("permission") || text.Contains("403") || text.Contains("forbidden"))
        {
            return "GitHub denied the push — the access token needs write (Contents) permission on this repository.";
        }

        if (text.Contains("authentication failed") || text.Contains("could not read username")
            || text.Contains("401") || text.Contains("invalid username or password"))
        {
            return "GitHub rejected the credentials — check the repository's access token is valid and not expired.";
        }

        if (text.Contains("protected branch") || text.Contains("gh006"))
        {
            return "The branch is protected — GitHub blocks direct pushes to it. Switch the job to " +
                   "Pull request mode, or allow this token's account to push to the branch.";
        }

        if (text.Contains("gh001") || text.Contains("exceeds github's file size limit"))
        {
            return "GitHub rejected a file over its 100 MB limit. Remove the oversized file from the " +
                   "workspace branch (or reduce the job's reference-data scope), then re-run.";
        }

        if (text.Contains("non-fast-forward") || text.Contains("fetch first") || text.Contains("rejected"))
        {
            return "The remote branch has commits Obsync does not have — pull/merge the branch, then re-run.";
        }

        if (text.Contains("could not resolve host") || text.Contains("unable to access") || text.Contains("timed out"))
        {
            return "Could not reach GitHub — check network connectivity and the repository URL.";
        }

        var firstLine = (error ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "See technical details for the git error." : firstLine.Trim();
    }

    // A successful run that skipped one or more objects is a partial success — surface it as a warning.
    private static void EscalateForSkips(SyncRun run, RunContext context)
    {
        if (context.Failed > 0 && run.Status is RunStatus.Succeeded or RunStatus.NoChanges)
        {
            run.Status = RunStatus.Warning;
            // The warning banner shows ErrorMessage; without it a skip-Warning looked like an
            // unexplained yellow badge. The per-object reasons are in the run's log details.
            run.ErrorMessage =
                $"{context.Failed:N0} object(s) could not be scripted and were skipped — " +
                "open the run's Logs and expand the warning entry for the per-object reasons.";
        }
    }

    private static string DescribeIdentity(ScriptedObjectIdentity identity) =>
        string.IsNullOrEmpty(identity.Schema) ? identity.Name : $"{identity.Schema}.{identity.Name}";

    // Reads .obsyncignore from the destination folder(s) in the workspace and merges the job's own
    // ignore patterns. For a multi-database job both the per-database folder and the shared
    // destination root apply. A missing file is fine (no rules).
    private static async Task<IgnoreRules> LoadIgnoreRulesAsync(
        RunContext context, string localPath, string dbFolder, CancellationToken cancellationToken)
    {
        var rules = new IgnoreRules();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RepositoryLayout.Combine(dbFolder, RepositoryLayout.IgnoreFile),
            RepositoryLayout.Combine(context.Job.DestinationFolder, RepositoryLayout.IgnoreFile),
        };

        foreach (var relative in candidates)
        {
            var path = Path.Combine(localPath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            var parsed = IgnoreRules.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            rules.Schemas.AddRange(parsed.Schemas);
            rules.ObjectPatterns.AddRange(parsed.ObjectPatterns);
            foreach (var type in parsed.Types)
            {
                rules.Types.Add(type);
            }
        }

        rules.AddObjectPatterns(context.Job.Selection.IgnorePatterns);
        return rules;
    }

    private async Task PersistStatesAsync(
        RunContext context, RunStatus status, string? commitSha, bool delivered, CancellationToken cancellationToken)
    {
        if (!delivered)
        {
            // The changeset never reached durable delivery (commit failed, or — in PR mode — the
            // push or pull request failed on a branch that is recut every run). Leave states,
            // deletion records, and watermarks untouched so the NEXT run re-detects and re-delivers
            // everything. Advancing them here would make every later run report "no changes" while
            // the repository silently misses this run's work.
            if (context.PendingStates.Count > 0 || context.PendingDeletedStateIds.Count > 0)
            {
                context.Log(SyncLogLevel.Warning,
                    $"The run's {context.PendingStates.Count + context.PendingDeletedStateIds.Count:N0} change(s) " +
                    "were not delivered — they will be re-detected and retried on the next run.");
            }

            return;
        }

        var committedAt = commitSha is null ? (DateTimeOffset?)null : _clock.UtcNow;
        foreach (var state in context.PendingStates)
        {
            state.LastStatus = status;
            state.LastCommitSha = commitSha;
            state.LastCommittedAt = committedAt;
        }

        // One transaction for the whole batch — a VLDB first run persists hundreds of thousands
        // of states, and per-row auto-commits would dominate the run time.
        await _objectStates.UpsertManyAsync(context.PendingStates, cancellationToken).ConfigureAwait(false);

        // Dropped objects: their files are gone from the delivered commit, so the rows can go too.
        await _objectStates.DeleteManyAsync(context.PendingDeletedStateIds, cancellationToken).ConfigureAwait(false);

        // Incremental watermarks only advance when the run reached a healthy final status — a
        // failed or cancelled run must re-examine everything past the last good watermark next time.
        if (status is RunStatus.Succeeded or RunStatus.NoChanges or RunStatus.Warning)
        {
            foreach (var (database, watermarks) in context.PendingWatermarks)
            {
                await _watermarks.UpsertManyAsync(context.Job.Id, database, watermarks, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteFileAsync(
        RunContext context, string localPath, string repoRelativePath, string content,
        string? localExportRoot, string dbFolder, string relativePath, CancellationToken cancellationToken)
    {
        var absolute = Path.Combine(localPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureDirectory(context, Path.GetDirectoryName(absolute)!);
        await WriteAtomicAsync(absolute, content, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(localExportRoot))
        {
            var exportRelative = RepositoryLayout.Combine(dbFolder, relativePath).Replace('/', Path.DirectorySeparatorChar);
            var exportPath = Path.Combine(localExportRoot, exportRelative);
            EnsureDirectory(context, Path.GetDirectoryName(exportPath)!);
            await WriteAtomicAsync(exportPath, content, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write-to-temp then rename: a cancelled or crashed write can never leave a truncated .sql in
    /// place — the change-detection self-heal is existence-only (it never re-reads content), so a
    /// torn file would otherwise be swept into the next commit as-is. The rename is atomic on NTFS;
    /// the temp name is cleaned up on failure here and excluded from git commits as a backstop for
    /// a hard process kill between write and rename.
    /// </summary>
    private static async Task WriteAtomicAsync(string absolute, string content, CancellationToken cancellationToken)
    {
        var temp = absolute + ".obsync-tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(temp, absolute, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best-effort: the git-side exclusion keeps a stray temp file out of commits.
            }

            throw;
        }
    }

    // Objects of a type share a handful of folders; without this cache a VLDB run issues one
    // CreateDirectory syscall per object (hundreds of thousands, nearly all redundant).
    // GetOrAdd (not TryAdd-then-create): with parallel workers, marking the directory as ensured
    // BEFORE creating it let a second worker write into a directory that did not exist yet —
    // an intermittent "could not find a part of the path" that failed the whole run. The factory
    // can run concurrently for one key, but CreateDirectory is idempotent, so that is harmless.
    private static void EnsureDirectory(RunContext context, string directory) =>
        context.EnsuredDirectories.GetOrAdd(directory, static d =>
        {
            Directory.CreateDirectory(d);
            return 0;
        });

    private static string StateKey(ScriptedObjectIdentity identity) => $"{(int)identity.Type}|{identity.Schema}|{identity.Name}";

    private static string StateKey(TrackedObjectState state) => $"{(int)state.ObjectType}|{state.SchemaName}|{state.ObjectName}";

    /// <summary>
    /// Builds the prior-state lookup, failing with an actionable message when a case-sensitive
    /// database contains two objects whose names differ only by letter case. All of Obsync's
    /// change-detection maps — and Windows file paths themselves — are case-insensitive, so such a
    /// pair cannot be tracked distinctly; without this check the duplicate key surfaced as an
    /// opaque crash on every run.
    /// </summary>
    private static Dictionary<string, TrackedObjectState> BuildPriorMap(
        IReadOnlyList<TrackedObjectState> states, string scope)
    {
        var map = new Dictionary<string, TrackedObjectState>(states.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (!map.TryAdd(StateKey(state), state))
            {
                var clash = map[StateKey(state)];
                throw new InvalidOperationException(
                    $"{scope} contains two objects whose names differ only by letter case " +
                    $"({clash.SchemaName}.{clash.ObjectName} and {state.SchemaName}.{state.ObjectName}). " +
                    "Windows file paths are case-insensitive, so Obsync cannot version them as separate files — " +
                    "rename one of the objects, or exclude one with an .obsyncignore rule.");
            }
        }

        return map;
    }

    /// <summary>Mutable per-run accumulator passed between the engine stages.</summary>
    private sealed class RunContext
    {
        private readonly IProgress<SyncProgress>? _progress;

        public RunContext(SyncJob job, SqlConnectionProfile connection, GitRepositoryProfile? repository,
            string? sqlPassword, string? gitToken, IProgress<SyncProgress>? progress)
        {
            Job = job;
            Connection = connection;
            Repository = repository;
            SqlPassword = sqlPassword;
            GitToken = gitToken;
            Databases = job.Databases;
            _progress = progress;
        }

        /// <summary>The concrete databases this run scripts — the job's fixed list, or the
        /// dynamic scope resolved against the live server at the start of the run.</summary>
        public IReadOnlyList<string> Databases { get; set; }

        public SyncJob Job { get; }
        public SqlConnectionProfile Connection { get; }
        public GitRepositoryProfile? Repository { get; }
        public string? SqlPassword { get; }
        public string? GitToken { get; }

        private int _scanned;
        private int _added;
        private int _modified;
        private int _deleted;
        private int _failed;

        // Counters are bumped from many worker threads, so reads/writes go through Interlocked.
        public int Scanned => Volatile.Read(ref _scanned);
        public int Added => Volatile.Read(ref _added);
        public int Modified => Volatile.Read(ref _modified);
        public int Deleted => Volatile.Read(ref _deleted);
        public int Failed => Volatile.Read(ref _failed);

        public void IncrementScanned() => Interlocked.Increment(ref _scanned);
        public void IncrementAdded() => Interlocked.Increment(ref _added);
        public void IncrementModified() => Interlocked.Increment(ref _modified);
        public void IncrementDeleted() => Interlocked.Increment(ref _deleted);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);

        // Changes are added concurrently by workers; Logs and PendingStates only from single-threaded stages.
        private readonly List<ObjectChange> _changes = [];
        private readonly Lock _changesGate = new();

        /// <summary>Records one change. Called concurrently by pipeline workers — a locked List is
        /// far cheaper at VLDB scale than a ConcurrentBag (no per-thread segments, no copy to read).</summary>
        public void AddChange(ObjectChange change)
        {
            lock (_changesGate)
            {
                _changes.Add(change);
            }
        }

        /// <summary>Read only after the parallel phase — finalize/persist run single-threaded.</summary>
        public IReadOnlyList<ObjectChange> Changes => _changes;

        /// <summary>Directories already created this run, so writers skip redundant syscalls.</summary>
        public ConcurrentDictionary<string, byte> EnsuredDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SyncRunLog> Logs { get; } = [];
        public List<TrackedObjectState> PendingStates { get; } = [];

        /// <summary>
        /// State-row ids of objects dropped from the database this run. Their files are removed from
        /// the working tree during scripting (so the commit carries the deletions), but the rows are
        /// only removed from tracked state after the changeset is durably delivered — otherwise a
        /// failed delivery would erase the record and the deletion would never be retried.
        /// </summary>
        public List<long> PendingDeletedStateIds { get; } = [];

        /// <summary>
        /// True once this run's changeset is durably delivered: committed for the direct and
        /// local-only modes (an unpushed direct commit is re-pushed by the next run), or the pull
        /// request opened for PR mode (whose head branch is recut from the base every run, so
        /// anything short of an opened PR is lost). This is the gate for advancing tracked object
        /// state — advancing it on a failed delivery makes every later run report "no changes"
        /// while the repository silently misses this run's work.
        /// </summary>
        public bool ChangesDelivered { get; set; }

        /// <summary>Per-database incremental watermarks staged during scripting; persisted with the
        /// final states only when the run ends healthy. Written from single-threaded stages.</summary>
        public Dictionary<string, IReadOnlyDictionary<SqlObjectType, DateTime>> PendingWatermarks { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void Report(SyncPhase phase, string message, int done = 0, int total = 0) =>
            _progress?.Report(new SyncProgress(phase, message, done, total));

        public void Log(SyncLogLevel level, string message, string? detail = null) =>
            Logs.Add(new SyncRunLog { RunId = Guid.Empty, Timestamp = DateTimeOffset.UtcNow, Level = level, Message = message, Detail = detail });
    }
}
