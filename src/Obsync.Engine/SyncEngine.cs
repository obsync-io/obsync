using System.Collections.Concurrent;
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
        var connection = await _connections.GetAsync(job.ConnectionProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job's SQL connection profile was not found.");
        var repository = await _repositories.GetAsync(job.RepositoryProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job's GitHub repository profile was not found.");

        var sqlPassword = connection.RequiresPassword
            ? _credentialStore.Retrieve(CredentialKeys.SqlPassword(connection.Id))
            : null;
        var gitToken = _credentialStore.Retrieve(CredentialKeys.GitHubToken(repository.Id));

        var started = _clock.UtcNow;
        var run = new SyncRun
        {
            RunKey = started.LocalDateTime.ToString("yyyyMMdd-HHmmss"),
            JobId = job.Id,
            JobName = job.Name,
            Trigger = trigger,
            Status = RunStatus.Running,
            ServerName = connection.ServerName,
            Databases = string.Join(", ", job.Databases),
            StartedAt = started,
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
            var completed = _clock.UtcNow;
            run.CompletedAt = completed;
            run.DurationMs = (long)(completed - started).TotalMilliseconds;
            run.ObjectsAdded = context.Added;
            run.ObjectsModified = context.Modified;
            run.ObjectsDeleted = context.Deleted;
            run.ObjectsScanned = context.Scanned;

            foreach (var log in context.Logs)
            {
                log.RunId = run.Id;
            }

            await _runs.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            await _runs.AddLogsAsync(context.Logs, cancellationToken).ConfigureAwait(false);
            await _runs.AddChangesAsync(run.Id, context.Changes, cancellationToken).ConfigureAwait(false);
            await _jobs.UpdateRunSummaryAsync(job.Id, new JobRunSummary
            {
                LastRunId = run.Id,
                LastStatus = run.Status,
                LastRunAt = run.StartedAt,
                LastChangeCount = run.ChangeCount,
                LastCommitSha = run.CommitSha,
                NextRunAt = job.RunSummary.NextRunAt,
            }, cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    private async Task ExecuteAsync(SyncRun run, RunContext context, CancellationToken cancellationToken)
    {
        // Pull request commit mode is a planned feature (see CommitMode.PullRequest). It is not wired
        // up yet, so fail fast and clearly rather than scripting a run that would silently never push.
        if (context.Job.CommitMode == CommitMode.PullRequest)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = "Pull request commit mode is not yet supported. Use direct commit.";
            context.Log(SyncLogLevel.Error, run.ErrorMessage);
            return;
        }

        context.Report(SyncPhase.Connecting, $"Connecting to {context.Connection.ServerName}…");

        var branch = string.IsNullOrWhiteSpace(context.Job.Branch) ? context.Repository.DefaultBranch : context.Job.Branch!;
        var localPath = Path.Combine(_options.WorkspacesRoot, context.Repository.Id.ToString("N"));
        var gitContext = new GitWorkspaceContext
        {
            RemoteUrl = context.Repository.EffectiveRemoteUrl,
            Branch = branch,
            LocalPath = localPath,
            AuthorizationHeader = context.GitToken is null ? null : GitHubService.BuildAuthorizationHeader(context.GitToken),
            CommitterName = "Obsync",
            CommitterEmail = _options.CommitterEmail,
            NetworkRetryCount = context.Job.Advanced.GitRetryCount,
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

    private async Task ScriptDatabaseAsync(
        SyncRun run, RunContext context, string database, IReadOnlyList<SqlObjectType> types,
        string localPath, CancellationToken cancellationToken)
    {
        var dbFolder = context.Job.Databases.Count > 1
            ? RepositoryLayout.Combine(context.Job.DestinationFolder, database)
            : context.Job.DestinationFolder;

        var prior = (await _objectStates.GetForJobDatabaseAsync(context.Job.Id, database, cancellationToken).ConfigureAwait(false))
            .ToDictionary(StateKey, StringComparer.OrdinalIgnoreCase);

        // Accumulators are written from many worker threads concurrently, so they are all thread-safe.
        // `prior` is only read during processing, so a plain dictionary is safe to share.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var changedStates = new ConcurrentBag<TrackedObjectState>();
        var inventory = new ConcurrentBag<ObjectInventoryEntry>();

        // Diff + write + record + track for a single scripted item (a SQL object or a synthetic
        // database artifact). Artifacts ride the same change-detection path so an options- or
        // permissions-only change still produces a commit, while an unchanged run still produces none.
        // Runs concurrently across workers for objects, then sequentially for the trailing artifacts.
        async Task ApplyItemAsync(ScriptedObjectIdentity identity, string rawScript, string relativePath, CancellationToken ct)
        {
            var isArtifact = identity.Type == SqlObjectType.DatabaseArtifact;
            var script = context.Job.Selection.NormalizeScripts ? _normalizer.Normalize(rawScript) : rawScript;
            var hash = _hasher.ComputeHash(script);
            var repoRelativePath = RepositoryLayout.Combine(dbFolder, relativePath);
            var key = StateKey(identity);
            seen.TryAdd(key, 0);

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
                return;
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

        context.Report(SyncPhase.Scripting, $"Scripting objects in {database}…");

        var workers = context.Job.Advanced.MaxParallelWorkers > 0
            ? context.Job.Advanced.MaxParallelWorkers
            : Environment.ProcessorCount;

        // A single producer streams from the providers (one SqlDataReader at a time) into a bounded
        // channel; the worker pool normalizes, hashes, diffs, and writes objects in parallel.
        await ChannelPipeline.RunAsync(
            StreamProvidersAsync(context, database, types, cancellationToken),
            (raw, ct) => ApplyItemAsync(raw.Identity, raw.Script, _pathMapper.MapRelativePath(raw.Identity), ct),
            workers,
            cancellationToken).ConfigureAwait(false);

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

        if (selection.IncludeObjectInventory)
        {
            var json = ObjectInventoryWriter.Serialize(context.Connection.ServerName, database, inventory);
            await apply(ArtifactIdentity("object-inventory"), json, RepositoryLayout.ObjectInventoryFile, cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeDatabaseOptions)
        {
            var options = await _artifactReader.ReadDatabaseOptionsAsync(
                context.Connection, context.SqlPassword, database, timeout, cancellationToken).ConfigureAwait(false);
            await apply(ArtifactIdentity("database-options"), options, RepositoryLayout.DatabaseOptionsFile, cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeDatabasePermissionsFile)
        {
            var permissions = await _artifactReader.ReadPermissionsAsync(
                context.Connection, context.SqlPassword, database, timeout, cancellationToken).ConfigureAwait(false);
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
            $"{context.Added} added, {context.Modified} modified, {context.Deleted} deleted.");

        if (context.Changes.Count == 0)
        {
            run.Status = RunStatus.NoChanges;
            context.Log(SyncLogLevel.Info, "No changes detected — nothing to commit.");
            await PersistStatesAsync(context, RunStatus.NoChanges, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        run.ObjectsAdded = context.Added;
        run.ObjectsModified = context.Modified;
        run.ObjectsDeleted = context.Deleted;
        run.ObjectsScanned = context.Scanned;

        context.Report(SyncPhase.Committing, "Creating commit…");
        var (subject, body) = CommitMessageBuilder.Build(run, context.Job, [.. context.Changes]);
        var commit = await _gitWorkspace.CommitAllAsync(gitContext, subject, body, cancellationToken).ConfigureAwait(false);

        if (!commit.Success)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = commit.Error;
            context.Log(SyncLogLevel.Error, "Commit failed.", commit.Error);
            await PersistStatesAsync(context, RunStatus.Failed, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!commit.HadChanges)
        {
            run.Status = RunStatus.NoChanges;
            await PersistStatesAsync(context, RunStatus.NoChanges, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sha = commit.CommitSha!;
        run.CommitSha = sha;
        run.CommitUrl = GitHubService.BuildCommitUrl(context.Repository.Owner, context.Repository.RepositoryName, sha);
        context.Log(SyncLogLevel.Info, $"Created commit {sha[..Math.Min(7, sha.Length)]}.");

        // Only direct-commit jobs reach here; pull request mode is rejected up front in ExecuteAsync.
        context.Report(SyncPhase.Pushing, "Pushing to GitHub…");
        var push = await _gitWorkspace.PushAsync(gitContext, cancellationToken).ConfigureAwait(false);
        if (push.IsFailure)
        {
            run.Status = RunStatus.Warning;
            context.Log(SyncLogLevel.Warning, "Commit created locally but the push to GitHub failed.", push.Error);
        }
        else
        {
            run.Status = RunStatus.Succeeded;
            context.Log(SyncLogLevel.Info, "Pushed to GitHub.");
        }

        await PersistStatesAsync(context, run.Status, commit.CommitSha, cancellationToken).ConfigureAwait(false);
        context.Report(SyncPhase.Completed, "Run completed.");
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

        public RunContext(SyncJob job, SqlConnectionProfile connection, GitRepositoryProfile repository,
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
        public GitRepositoryProfile Repository { get; }
        public string? SqlPassword { get; }
        public string? GitToken { get; }

        private int _scanned;
        private int _added;
        private int _modified;
        private int _deleted;

        // Counters are bumped from many worker threads, so reads/writes go through Interlocked.
        public int Scanned => Volatile.Read(ref _scanned);
        public int Added => Volatile.Read(ref _added);
        public int Modified => Volatile.Read(ref _modified);
        public int Deleted => Volatile.Read(ref _deleted);

        public void IncrementScanned() => Interlocked.Increment(ref _scanned);
        public void IncrementAdded() => Interlocked.Increment(ref _added);
        public void IncrementModified() => Interlocked.Increment(ref _modified);
        public void IncrementDeleted() => Interlocked.Increment(ref _deleted);

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
