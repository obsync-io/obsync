using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Obsync.Data.Repositories;
using Obsync.Git;
using Obsync.GitHub;
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

    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IRunRepository _runs;
    private readonly IObjectStateRepository _objectStates;
    private readonly IReadOnlyList<IObjectScriptProvider> _scriptProviders;
    private readonly IDatabaseArtifactReader _artifactReader;
    private readonly IGitWorkspace _gitWorkspace;
    private readonly IGitHubService _gitHub;
    private readonly IProxyProvider _proxy;
    private readonly ICredentialStore _credentialStore;
    private readonly IScriptNormalizer _normalizer;
    private readonly IObjectHasher _hasher;
    private readonly IObjectFilePathMapper _pathMapper;
    private readonly IClock _clock;
    private readonly ObsyncEngineOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IRunRepository runs,
        IObjectStateRepository objectStates,
        IEnumerable<IObjectScriptProvider> scriptProviders,
        IDatabaseArtifactReader artifactReader,
        IGitWorkspace gitWorkspace,
        IGitHubService gitHub,
        IProxyProvider proxy,
        ICredentialStore credentialStore,
        IScriptNormalizer normalizer,
        IObjectHasher hasher,
        IObjectFilePathMapper pathMapper,
        IClock clock,
        IOptions<ObsyncEngineOptions> options,
        ILogger<SyncEngine> logger)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _runs = runs;
        _objectStates = objectStates;
        _scriptProviders = [.. scriptProviders];
        _artifactReader = artifactReader;
        _gitWorkspace = gitWorkspace;
        _gitHub = gitHub;
        _proxy = proxy;
        _credentialStore = credentialStore;
        _normalizer = normalizer;
        _hasher = hasher;
        _pathMapper = pathMapper;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncRun> RunJobAsync(
        Guid jobId, RunTrigger trigger, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId} was not found.");

        // Maintenance window: a SCHEDULED run outside the allowed window is skipped (manual "Run Now"
        // and startup runs bypass). Skips are log-only — advance the cached next-run so the UI stays
        // accurate, and return an un-persisted run (the scheduler ignores the result).
        if (trigger == RunTrigger.Scheduled && !job.Schedule.IsWithinMaintenanceWindow(_clock.UtcNow.ToLocalTime()))
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
            Databases = string.Join(", ", job.Databases),
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
        }

        return run;
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
        var localPath = Path.Combine(_options.WorkspacesRoot, context.Repository!.Id.ToString("N"));
        var proxyUrl = (await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false))?.GitProxyUrl;
        var gitContext = new GitWorkspaceContext
        {
            RemoteUrl = context.Repository.EffectiveRemoteUrl,
            Branch = headBranch,
            BaseBranch = isPullRequest ? baseBranch : null,
            LocalPath = localPath,
            AuthorizationHeader = context.GitToken is null ? null : GitHubService.BuildAuthorizationHeader(context.GitToken),
            CommitterName = "Obsync",
            CommitterEmail = _options.CommitterEmail,
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

        var types = context.Job.Selection.ResolveTypes();
        foreach (var database in context.Job.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScriptDatabaseAsync(run, context, database, types, localPath, cancellationToken).ConfigureAwait(false);
        }

        await FinalizeAsync(run, context, gitContext, cancellationToken).ConfigureAwait(false);
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

        var types = context.Job.Selection.ResolveTypes();
        foreach (var database in context.Job.Databases)
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
        var dbFolder = context.Job.Databases.Count > 1
            ? RepositoryLayout.Combine(context.Job.DestinationFolder, database)
            : context.Job.DestinationFolder;

        // Export Only writes a full snapshot: an empty prior map makes every object "Added" (so all
        // are written), the deletion pass a no-op, and no object_states are consulted.
        var prior = fullSnapshot
            ? new Dictionary<string, TrackedObjectState>(StringComparer.OrdinalIgnoreCase)
            : (await _objectStates.GetForJobDatabaseAsync(context.Job.Id, database, cancellationToken).ConfigureAwait(false))
                .ToDictionary(StateKey, StringComparer.OrdinalIgnoreCase);

        // Accumulators are written from many worker threads concurrently, so they are all thread-safe.
        // `prior` is only read during processing, so a plain dictionary is safe to share.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var changedStates = new ConcurrentBag<TrackedObjectState>();
        var inventory = new ConcurrentBag<ObjectInventoryEntry>();
        var skipped = new ConcurrentBag<string>();
        var ignoreRules = await LoadIgnoreRulesAsync(context, localPath, dbFolder, cancellationToken).ConfigureAwait(false);

        // Diff + write + record + track for a single scripted item (a SQL object or a synthetic
        // database artifact). Artifacts ride the same change-detection path so an options- or
        // permissions-only change still produces a commit, while an unchanged run still produces none.
        // Runs concurrently across workers for objects, then sequentially for the trailing artifacts.
        async Task ApplyItemAsync(ScriptedObjectIdentity identity, string rawScript, string relativePath, CancellationToken ct)
        {
            var isArtifact = identity.Type == SqlObjectType.DatabaseArtifact;
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
            }

            var hasPrior = prior.TryGetValue(key, out var priorState);
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

            await WriteFileAsync(localPath, repoRelativePath, script, context.Job.LocalExportPath, dbFolder, relativePath, ct)
                .ConfigureAwait(false);

            if (changeType == ChangeType.Added)
            {
                context.IncrementAdded();
            }
            else
            {
                context.IncrementModified();
            }

            context.Changes.Add(new ObjectChange
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
        Task RecordSkipAsync(RawScriptedObject raw)
        {
            seen.TryAdd(StateKey(raw.Identity), 0);
            context.IncrementScanned();
            context.IncrementFailed();
            skipped.Add($"{raw.Identity.Type} {DescribeIdentity(raw.Identity)} — {raw.SkipReason}");
            return Task.CompletedTask;
        }

        context.Report(SyncPhase.Scripting, $"Scripting objects in {database}…");

        var workers = context.Job.Advanced.MaxParallelWorkers > 0
            ? context.Job.Advanced.MaxParallelWorkers
            : Environment.ProcessorCount;

        // A single producer streams from the providers (one SqlDataReader at a time) into a bounded
        // channel; the worker pool normalizes, hashes, diffs, and writes objects in parallel.
        await ChannelPipeline.RunAsync(
            StreamProvidersAsync(context, database, types, cancellationToken),
            (raw, ct) => raw.SkipReason is not null
                ? RecordSkipAsync(raw)
                : ApplyItemAsync(raw.Identity, raw.Script, _pathMapper.MapRelativePath(raw.Identity), ct),
            workers,
            cancellationToken).ConfigureAwait(false);

        if (!skipped.IsEmpty)
        {
            var details = skipped.ToList();
            context.Log(SyncLogLevel.Warning,
                $"{details.Count:N0} object(s) in {database} could not be scripted and were skipped.",
                string.Join("\n", details.Take(100)));
        }

        // Artifacts and deletions run after the parallel phase, so they touch the accumulators alone.
        await GenerateDatabaseArtifactsAsync(context, database, [.. inventory], ApplyItemAsync, cancellationToken).ConfigureAwait(false);

        await ApplyDeletionsAsync(context, database, localPath, prior, seen, cancellationToken).ConfigureAwait(false);

        context.PendingStates.AddRange(changedStates);
    }

    /// <summary>
    /// Streams every requested object across providers in sequence — metadata fast-path first, then
    /// SMO — yielding one <see cref="RawScriptedObject"/> at a time. A single consumer (the pipeline
    /// producer) advances this, so the providers' SqlDataReaders are never touched concurrently.
    /// </summary>
    private async IAsyncEnumerable<RawScriptedObject> StreamProvidersAsync(
        RunContext context, string database, IReadOnlyList<SqlObjectType> types,
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
        Func<ScriptedObjectIdentity, string, string, CancellationToken, Task> apply, CancellationToken cancellationToken)
    {
        var selection = context.Job.Selection;
        var timeout = context.Job.Advanced.SqlCommandTimeoutSeconds;
        var lockTimeout = context.Job.Advanced.SqlLockTimeoutSeconds;

        if (selection.IncludeObjectInventory)
        {
            var json = ObjectInventoryWriter.Serialize(context.Connection.ServerName, database, inventory);
            await apply(ArtifactIdentity("object-inventory"), json, RepositoryLayout.ObjectInventoryFile, cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeDatabaseOptions)
        {
            var options = await _artifactReader.ReadDatabaseOptionsAsync(
                context.Connection, context.SqlPassword, database, timeout, lockTimeout, cancellationToken).ConfigureAwait(false);
            await apply(ArtifactIdentity("database-options"), options, RepositoryLayout.DatabaseOptionsFile, cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeDatabasePermissionsFile)
        {
            var permissions = await _artifactReader.ReadPermissionsAsync(
                context.Connection, context.SqlPassword, database, timeout, lockTimeout, cancellationToken).ConfigureAwait(false);
            await apply(ArtifactIdentity("permissions"), permissions, RepositoryLayout.PermissionsFile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ScriptedObjectIdentity ArtifactIdentity(string name) =>
        new(SqlObjectType.DatabaseArtifact, string.Empty, name);

    private async Task ApplyDeletionsAsync(
        RunContext context, string database, string localPath,
        Dictionary<string, TrackedObjectState> prior, ConcurrentDictionary<string, byte> seen, CancellationToken cancellationToken)
    {
        if (!context.Job.Selection.RemoveDroppedObjects)
        {
            return;
        }

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
            context.Changes.Add(new ObjectChange
            {
                ChangeType = ChangeType.Deleted,
                ObjectType = state.ObjectType,
                Schema = state.SchemaName,
                Name = state.ObjectName,
                RelativePath = state.FilePath,
                PreviousHash = state.LastHash,
            });

            await _objectStates.DeleteAsync(state.Id, cancellationToken).ConfigureAwait(false);
        }
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

            await PersistStatesAsync(context, run.Status, run.CommitSha, cancellationToken).ConfigureAwait(false);
            EscalateForSkips(run, context);
            context.Report(SyncPhase.Completed, "Run completed.");
            return;
        }

        await CommitAndPushAsync(run, context, gitContext, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        await PersistStatesAsync(context, run.Status, run.CommitSha, cancellationToken).ConfigureAwait(false);
        EscalateForSkips(run, context);
        context.Report(SyncPhase.Completed, "Run completed.");
    }

    private async Task CommitAndPushAsync(
        SyncRun run, RunContext context, GitWorkspaceContext gitContext, bool allowEmpty, CancellationToken cancellationToken)
    {
        context.Report(SyncPhase.Committing, "Creating commit…");
        var (subject, body) = CommitMessageBuilder.Build(run, context.Job, [.. context.Changes]);
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
            run.Status = RunStatus.NoChanges;
            return;
        }

        // Local Commit Only stops here: the commit lives in the local clone, to be pushed later.
        if (isLocalOnly)
        {
            run.Status = RunStatus.Succeeded;
            context.Log(SyncLogLevel.Info, "Committed locally (not pushed).");
            return;
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
            run.ErrorMessage = push.Error;
            // Put the real reason in the visible (non-technical) message — a hidden push failure is
            // exactly what stops objects from reaching GitHub. Add a hint for the most common cause.
            var reason = ExplainPushFailure(push.Error);
            context.Log(SyncLogLevel.Warning, $"The commit was created locally but the push to GitHub failed. {reason}", push.Error);
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

    private async Task PersistStatesAsync(RunContext context, RunStatus status, string? commitSha, CancellationToken cancellationToken)
    {
        var committedAt = commitSha is null ? (DateTimeOffset?)null : _clock.UtcNow;
        foreach (var state in context.PendingStates)
        {
            state.LastStatus = status;
            state.LastCommitSha = commitSha;
            state.LastCommittedAt = committedAt;
            await _objectStates.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileAsync(
        string localPath, string repoRelativePath, string content,
        string? localExportRoot, string dbFolder, string relativePath, CancellationToken cancellationToken)
    {
        var absolute = Path.Combine(localPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(localExportRoot))
        {
            var exportRelative = RepositoryLayout.Combine(dbFolder, relativePath).Replace('/', Path.DirectorySeparatorChar);
            var exportPath = Path.Combine(localExportRoot, exportRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            await File.WriteAllTextAsync(exportPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string StateKey(ScriptedObjectIdentity identity) => $"{(int)identity.Type}|{identity.Schema}|{identity.Name}";

    private static string StateKey(TrackedObjectState state) => $"{(int)state.ObjectType}|{state.SchemaName}|{state.ObjectName}";

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
            _progress = progress;
        }

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
        public ConcurrentBag<ObjectChange> Changes { get; } = new();
        public List<SyncRunLog> Logs { get; } = [];
        public List<TrackedObjectState> PendingStates { get; } = [];

        public void Report(SyncPhase phase, string message, int done = 0, int total = 0) =>
            _progress?.Report(new SyncProgress(phase, message, done, total));

        public void Log(SyncLogLevel level, string message, string? detail = null) =>
            Logs.Add(new SyncRunLog { RunId = Guid.Empty, Timestamp = DateTimeOffset.UtcNow, Level = level, Message = message, Detail = detail });
    }
}
