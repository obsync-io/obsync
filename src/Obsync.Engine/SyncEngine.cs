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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedStates = new List<TrackedObjectState>();

        context.Report(SyncPhase.Scripting, $"Scripting objects in {database}…");

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
            };

            await foreach (var raw in provider.ScriptAsync(request, cancellationToken).ConfigureAwait(false))
            {
                context.Scanned++;
                var script = context.Job.Selection.NormalizeScripts ? _normalizer.Normalize(raw.Script) : raw.Script;
                var hash = _hasher.ComputeHash(script);
                var relativePath = _pathMapper.MapRelativePath(raw.Identity);
                var repoRelativePath = RepositoryLayout.Combine(dbFolder, relativePath);
                var key = StateKey(raw.Identity);
                seen.Add(key);

                var hasPrior = prior.TryGetValue(key, out var priorState);
                var changeType = !hasPrior ? ChangeType.Added : priorState!.LastHash == hash ? ChangeType.Unchanged : ChangeType.Modified;
                if (changeType == ChangeType.Unchanged)
                {
                    continue;
                }

                await WriteFileAsync(localPath, repoRelativePath, script, context.Job.LocalExportPath, dbFolder, relativePath, cancellationToken)
                    .ConfigureAwait(false);

                if (changeType == ChangeType.Added)
                {
                    context.Added++;
                }
                else
                {
                    context.Modified++;
                }

                context.Changes.Add(new ObjectChange
                {
                    ChangeType = changeType,
                    ObjectType = raw.Identity.Type,
                    Schema = raw.Identity.Schema,
                    Name = raw.Identity.Name,
                    RelativePath = repoRelativePath,
                    PreviousHash = hasPrior ? priorState!.LastHash : null,
                    NewHash = hash,
                });

                changedStates.Add(new TrackedObjectState
                {
                    JobId = context.Job.Id,
                    DatabaseName = database,
                    ObjectType = raw.Identity.Type,
                    SchemaName = raw.Identity.Schema,
                    ObjectName = raw.Identity.Name,
                    ObjectId = raw.Identity.ObjectId,
                    FilePath = repoRelativePath,
                    LastHash = hash,
                    LastScriptedAt = _clock.UtcNow,
                    LastRunId = run.Id,
                    LastStatus = RunStatus.Running,
                });
            }
        }

        await ApplyDeletionsAsync(context, database, localPath, prior, seen, cancellationToken).ConfigureAwait(false);

        context.PendingStates.AddRange(changedStates);
    }

    private async Task ApplyDeletionsAsync(
        RunContext context, string database, string localPath,
        Dictionary<string, TrackedObjectState> prior, HashSet<string> seen, CancellationToken cancellationToken)
    {
        if (!context.Job.Selection.RemoveDroppedObjects)
        {
            return;
        }

        foreach (var (key, state) in prior)
        {
            if (seen.Contains(key))
            {
                continue;
            }

            var absolute = Path.Combine(localPath, state.FilePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
            }

            context.Deleted++;
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
        var (subject, body) = CommitMessageBuilder.Build(run, context.Job, context.Changes);
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

        if (context.Job.CommitMode == CommitMode.DirectCommit)
        {
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
        }
        else
        {
            run.Status = RunStatus.Succeeded;
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

        public int Scanned { get; set; }
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }

        public List<ObjectChange> Changes { get; } = [];
        public List<SyncRunLog> Logs { get; } = [];
        public List<TrackedObjectState> PendingStates { get; } = [];

        public void Report(SyncPhase phase, string message, int done = 0, int total = 0) =>
            _progress?.Report(new SyncProgress(phase, message, done, total));

        public void Log(SyncLogLevel level, string message, string? detail = null) =>
            Logs.Add(new SyncRunLog { RunId = Guid.Empty, Timestamp = DateTimeOffset.UtcNow, Level = level, Message = message, Detail = detail });
    }
}
