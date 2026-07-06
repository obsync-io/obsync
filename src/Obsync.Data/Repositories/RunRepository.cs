using Dapper;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for runs, their log entries, and their per-object changes.</summary>
public interface IRunRepository
{
    Task InsertAsync(SyncRun run, CancellationToken cancellationToken = default);
    Task UpdateAsync(SyncRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks runs left in <see cref="RunStatus.Running"/> since before <paramref name="staleBefore"/>
    /// as failed. A run executes in-process, so any still "Running" after the app restarts is orphaned
    /// (e.g. the app was killed mid-run, or a past concurrency bug). Returns how many were reconciled.
    /// </summary>
    Task<int> FailStaleRunningAsync(DateTimeOffset staleBefore, DateTimeOffset completedAt, string reason, CancellationToken cancellationToken = default);
    Task<SyncRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRun>> GetForJobAsync(Guid jobId, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRun>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default);

    Task AddLogsAsync(IReadOnlyCollection<SyncRunLog> logs, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRunLog>> GetLogsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task AddChangesAsync(Guid runId, IReadOnlyCollection<ObjectChange> changes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ObjectChange>> GetChangesAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes runs (with their logs and changes, via cascade) that started before the cutoff.
    /// In-flight runs are never touched. Returns how many runs were removed.
    /// </summary>
    Task<int> DeleteRunsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many non-manual (scheduled/startup) runs failed or ended with warnings since a timestamp —
    /// drives the "runs failed while you were away" notification on app startup.
    /// </summary>
    Task<int> CountUnattendedFailuresSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRunRepository" />
public sealed class RunRepository : IRunRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RunRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    private const string SelectRun = """
        SELECT id AS Id, run_key AS RunKey, job_id AS JobId, job_name AS JobName, trigger AS Trigger,
               triggered_by AS TriggeredBy,
               status AS Status, server_name AS ServerName, databases AS Databases, started_at AS StartedAt,
               completed_at AS CompletedAt, duration_ms AS DurationMs, objects_scanned AS ObjectsScanned,
               objects_added AS ObjectsAdded, objects_modified AS ObjectsModified, objects_deleted AS ObjectsDeleted,
               objects_failed AS ObjectsFailed, commit_sha AS CommitSha, commit_url AS CommitUrl,
               pr_url AS PullRequestUrl, pr_number AS PullRequestNumber,
               error_message AS ErrorMessage, tags_json AS TagsJson
        FROM runs
        """;

    public async Task InsertAsync(SyncRun run, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs
                (id, run_key, job_id, job_name, trigger, triggered_by, status, server_name, databases, started_at, completed_at,
                 duration_ms, objects_scanned, objects_added, objects_modified, objects_deleted, objects_failed,
                 commit_sha, commit_url, pr_url, pr_number, error_message, tags_json)
            VALUES
                ($id, $key, $job, $jobName, $trigger, $triggeredBy, $status, $server, $dbs, $started, $completed, $duration,
                 $scanned, $added, $modified, $deleted, $failed, $sha, $url, $prUrl, $prNumber, $error, $tags);
            """,
            ToParameters(run), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateAsync(SyncRun run, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE runs SET
                status = $status, databases = $dbs, completed_at = $completed, duration_ms = $duration,
                objects_scanned = $scanned, objects_added = $added, objects_modified = $modified,
                objects_deleted = $deleted, objects_failed = $failed, commit_sha = $sha, commit_url = $url,
                pr_url = $prUrl, pr_number = $prNumber, error_message = $error
            WHERE id = $id;
            """,
            ToParameters(run), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> FailStaleRunningAsync(
        DateTimeOffset staleBefore, DateTimeOffset completedAt, string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE runs
            SET status = $failed, completed_at = $completed, error_message = $reason
            WHERE status = $running AND started_at < $stale;
            """,
            new
            {
                failed = (int)RunStatus.Failed,
                running = (int)RunStatus.Running,
                completed = completedAt,
                reason,
                stale = staleBefore,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteRunsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM runs WHERE started_at < $cutoff AND status <> $running;",
            new { cutoff, running = (int)RunStatus.Running },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountUnattendedFailuresSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM runs
            WHERE started_at > $since AND "trigger" <> $manual AND status IN ($failed, $warning);
            """,
            new
            {
                since,
                manual = (int)RunTrigger.Manual,
                failed = (int)RunStatus.Failed,
                warning = (int)RunStatus.Warning,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SyncRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(
            new CommandDefinition($"{SelectRun} WHERE id = $id;", new { id = runId.ToString() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<SyncRun>> GetForJobAsync(Guid jobId, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RunRow>(
            new CommandDefinition($"{SelectRun} WHERE job_id = $job ORDER BY started_at DESC LIMIT $limit;",
                new { job = jobId.ToString(), limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<SyncRun>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RunRow>(
            new CommandDefinition($"{SelectRun} ORDER BY started_at DESC LIMIT $limit;",
                new { limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task AddLogsAsync(IReadOnlyCollection<SyncRunLog> logs, CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO run_logs (run_id, timestamp, level, message, detail)
            VALUES ($run, $timestamp, $level, $message, $detail);
            """,
            logs.Select(l => new
            {
                run = l.RunId.ToString(),
                timestamp = l.Timestamp,
                level = (int)l.Level,
                message = l.Message,
                detail = l.Detail,
            }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncRunLog>> GetLogsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SyncRunLog>(new CommandDefinition(
            """
            SELECT id AS Id, run_id AS RunId, timestamp AS Timestamp, level AS Level, message AS Message, detail AS Detail
            FROM run_logs WHERE run_id = $run ORDER BY id;
            """,
            new { run = runId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows];
    }

    public async Task AddChangesAsync(Guid runId, IReadOnlyCollection<ObjectChange> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO run_changes (run_id, change_type, object_type, schema_name, object_name, relative_path, previous_hash, new_hash)
            VALUES ($run, $change, $type, $schema, $name, $path, $prev, $new);
            """,
            changes.Select(c => new
            {
                run = runId.ToString(),
                change = (int)c.ChangeType,
                type = (int)c.ObjectType,
                schema = c.Schema,
                name = c.Name,
                path = c.RelativePath,
                prev = c.PreviousHash,
                @new = c.NewHash,
            }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ObjectChange>> GetChangesAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            """
            SELECT change_type AS ChangeType, object_type AS ObjectType, schema_name AS SchemaName,
                   object_name AS ObjectName, relative_path AS RelativePath, previous_hash AS PreviousHash, new_hash AS NewHash
            FROM run_changes WHERE run_id = $run ORDER BY change_type, schema_name, object_name;
            """,
            new { run = runId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new ObjectChange
        {
            ChangeType = (ChangeType)r.ChangeType,
            ObjectType = (SqlObjectType)r.ObjectType,
            Schema = r.SchemaName,
            Name = r.ObjectName,
            RelativePath = r.RelativePath,
            PreviousHash = r.PreviousHash,
            NewHash = r.NewHash,
        })];
    }

    private static object ToParameters(SyncRun run) => new
    {
        id = run.Id.ToString(),
        key = run.RunKey,
        job = run.JobId.ToString(),
        jobName = run.JobName,
        trigger = (int)run.Trigger,
        triggeredBy = run.TriggeredBy,
        status = (int)run.Status,
        server = run.ServerName,
        dbs = run.Databases,
        started = run.StartedAt,
        completed = run.CompletedAt,
        duration = run.DurationMs,
        scanned = run.ObjectsScanned,
        added = run.ObjectsAdded,
        modified = run.ObjectsModified,
        deleted = run.ObjectsDeleted,
        failed = run.ObjectsFailed,
        sha = run.CommitSha,
        url = run.CommitUrl,
        prUrl = run.PullRequestUrl,
        prNumber = run.PullRequestNumber,
        error = run.ErrorMessage,
        tags = ObsyncJson.Serialize(run.Tags),
    };

    private static SyncRun Map(RunRow row) => new()
    {
        Id = row.Id,
        RunKey = row.RunKey,
        JobId = row.JobId,
        JobName = row.JobName,
        Trigger = row.Trigger,
        TriggeredBy = row.TriggeredBy,
        Status = row.Status,
        ServerName = row.ServerName,
        Databases = row.Databases,
        StartedAt = row.StartedAt,
        CompletedAt = row.CompletedAt,
        DurationMs = row.DurationMs,
        ObjectsScanned = row.ObjectsScanned,
        ObjectsAdded = row.ObjectsAdded,
        ObjectsModified = row.ObjectsModified,
        ObjectsDeleted = row.ObjectsDeleted,
        ObjectsFailed = row.ObjectsFailed,
        CommitSha = row.CommitSha,
        CommitUrl = row.CommitUrl,
        PullRequestUrl = row.PullRequestUrl,
        PullRequestNumber = row.PullRequestNumber,
        ErrorMessage = row.ErrorMessage,
        Tags = string.IsNullOrEmpty(row.TagsJson) ? [] : ObsyncJson.Deserialize<List<string>>(row.TagsJson),
    };

    private sealed class RunRow
    {
        public Guid Id { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public Guid JobId { get; set; }
        public string JobName { get; set; } = string.Empty;
        public RunTrigger Trigger { get; set; }
        public string? TriggeredBy { get; set; }
        public RunStatus Status { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string Databases { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public long DurationMs { get; set; }
        public int ObjectsScanned { get; set; }
        public int ObjectsAdded { get; set; }
        public int ObjectsModified { get; set; }
        public int ObjectsDeleted { get; set; }
        public int ObjectsFailed { get; set; }
        public string? CommitSha { get; set; }
        public string? CommitUrl { get; set; }
        public string? PullRequestUrl { get; set; }
        public int? PullRequestNumber { get; set; }
        public string? ErrorMessage { get; set; }
        public string? TagsJson { get; set; }
    }

    private sealed class ChangeRow
    {
        public long ChangeType { get; set; }
        public long ObjectType { get; set; }
        public string SchemaName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string? PreviousHash { get; set; }
        public string? NewHash { get; set; }
    }
}
