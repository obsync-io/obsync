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

    /// <summary>All runs currently in <see cref="RunStatus.Running"/> (feeds orphaned-run recovery).</summary>
    Task<IReadOnlyList<SyncRun>> GetRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one run as failed with a reason, but only if it is still Running — a run that completed
    /// in the meantime is left untouched.
    /// </summary>
    Task FailRunAsync(Guid runId, DateTimeOffset completedAt, string reason, CancellationToken cancellationToken = default);
    Task<SyncRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRun>> GetForJobAsync(Guid jobId, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRun>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default);

    Task AddLogsAsync(IReadOnlyCollection<SyncRunLog> logs, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRunLog>> GetLogsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task AddChangesAsync(Guid runId, IReadOnlyCollection<ObjectChange> changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// The run's changes ordered by change type, schema, then name. A VLDB run can record hundreds of
    /// thousands, so display callers pass a <paramref name="limit"/> (0 = all, e.g. for report export).
    /// </summary>
    Task<IReadOnlyList<ObjectChange>> GetChangesAsync(Guid runId, int limit = 0, CancellationToken cancellationToken = default);

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

    public async Task<IReadOnlyList<SyncRun>> GetRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            $"{SelectRun} WHERE status = $running;",
            new { running = (int)RunStatus.Running }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task FailRunAsync(
        Guid runId, DateTimeOffset completedAt, string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE runs
            SET status = $failed, completed_at = $completed, error_message = $reason
            WHERE id = $id AND status = $running;
            """,
            new
            {
                failed = (int)RunStatus.Failed,
                running = (int)RunStatus.Running,
                completed = completedAt,
                reason,
                id = runId.ToString(),
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

    /// <summary>
    /// Rows per multi-row run_logs insert: 5 parameters per row × 560 rows = 2,800 parameters,
    /// comfortably under the bundled e_sqlite3's 32,766-variable limit
    /// (SQLITE_LIMIT_VARIABLE_NUMBER, verified empirically by BatchInsertChunkingTests).
    /// </summary>
    internal const int LogChunkRows = 560;

    private static string BuildLogsInsertSql(int rowCount)
    {
        var values = string.Join(",\n    ", Enumerable.Range(0, rowCount).Select(i =>
            $"($run{i}, $timestamp{i}, $level{i}, $message{i}, $detail{i})"));
        return $"""
            INSERT INTO run_logs (run_id, timestamp, level, message, detail)
            VALUES
                {values};
            """;
    }

    private static readonly string FullChunkLogsSql = BuildLogsInsertSql(LogChunkRows);

    public async Task AddLogsAsync(IReadOnlyCollection<SyncRunLog> logs, CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction for the batch (one journal fsync total), and multi-row VALUES chunks so
        // each command inserts LogChunkRows rows instead of paying a command round-trip per row.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in logs.Chunk(LogChunkRows))
        {
            var sql = chunk.Length == LogChunkRows ? FullChunkLogsSql : BuildLogsInsertSql(chunk.Length);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Length; i++)
            {
                var log = chunk[i];
                parameters.Add($"run{i}", log.RunId.ToString());
                parameters.Add($"timestamp{i}", log.Timestamp);
                parameters.Add($"level{i}", (int)log.Level);
                parameters.Add($"message{i}", log.Message);
                parameters.Add($"detail{i}", log.Detail);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Rows per multi-row run_changes insert: 8 parameters per row × 350 rows = 2,800 parameters,
    /// comfortably under the bundled e_sqlite3's 32,766-variable limit
    /// (SQLITE_LIMIT_VARIABLE_NUMBER, verified empirically by BatchInsertChunkingTests).
    /// </summary>
    internal const int ChangeChunkRows = 350;

    private static string BuildChangesInsertSql(int rowCount)
    {
        var values = string.Join(",\n    ", Enumerable.Range(0, rowCount).Select(i =>
            $"($run{i}, $change{i}, $type{i}, $schema{i}, $name{i}, $path{i}, $prev{i}, $new{i})"));
        return $"""
            INSERT INTO run_changes (run_id, change_type, object_type, schema_name, object_name, relative_path, previous_hash, new_hash)
            VALUES
                {values};
            """;
    }

    private static readonly string FullChunkChangesSql = BuildChangesInsertSql(ChangeChunkRows);

    public async Task AddChangesAsync(Guid runId, IReadOnlyCollection<ObjectChange> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction for the batch — a VLDB first run records every object as an Added change,
        // so this insert can carry hundreds of thousands of rows. Multi-row VALUES chunks keep it to
        // one command per ChangeChunkRows rows instead of a command round-trip per row.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = runId.ToString();
        foreach (var chunk in changes.Chunk(ChangeChunkRows))
        {
            var sql = chunk.Length == ChangeChunkRows ? FullChunkChangesSql : BuildChangesInsertSql(chunk.Length);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Length; i++)
            {
                var change = chunk[i];
                parameters.Add($"run{i}", run);
                parameters.Add($"change{i}", (int)change.ChangeType);
                parameters.Add($"type{i}", (int)change.ObjectType);
                parameters.Add($"schema{i}", change.Schema);
                parameters.Add($"name{i}", change.Name);
                parameters.Add($"path{i}", change.RelativePath);
                parameters.Add($"prev{i}", change.PreviousHash);
                parameters.Add($"new{i}", change.NewHash);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ObjectChange>> GetChangesAsync(Guid runId, int limit = 0, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            $"""
            SELECT change_type AS ChangeType, object_type AS ObjectType, schema_name AS SchemaName,
                   object_name AS ObjectName, relative_path AS RelativePath, previous_hash AS PreviousHash, new_hash AS NewHash
            FROM run_changes WHERE run_id = $run ORDER BY change_type, schema_name, object_name{(limit > 0 ? " LIMIT $limit" : string.Empty)};
            """,
            new { run = runId.ToString(), limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);

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
