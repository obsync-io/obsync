using Dapper;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for per-object tracking state (hashes and commit metadata).</summary>
public interface IObjectStateRepository
{
    Task<IReadOnlyList<TrackedObjectState>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default);

    /// <summary>
    /// The change-detection projection of <see cref="GetForJobDatabaseAsync"/>: identity, file
    /// path, and last hash only. The engine holds one of these per tracked object for a whole
    /// database pass — at VLDB scale the display-only columns of the wide row (timestamps, commit
    /// SHA, status, error text) roughly double the resident memory for data the engine never reads.
    /// </summary>
    Task<IReadOnlyList<TrackedObjectState>> GetTrackingStatesAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches a job database's indexed objects by name (or <c>schema.name</c>), capped for display.
    /// An empty query returns the first objects alphabetically. Synthetic engine artifacts are
    /// excluded — only real catalog objects can have dependencies.
    /// </summary>
    Task<IReadOnlyList<TrackedObjectState>> SearchAsync(
        Guid jobId, string database, string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>The distinct database names a job has indexed objects for.</summary>
    Task<IReadOnlyList<string>> GetDatabasesForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task UpsertAsync(TrackedObjectState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a batch on ONE connection inside ONE transaction. On a VLDB first run this persists
    /// hundreds of thousands of states — per-row connections/auto-commits would take longer than
    /// the scripting itself.
    /// </summary>
    Task UpsertManyAsync(IReadOnlyCollection<TrackedObjectState> states, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a batch of state rows in one transaction (mass-drop handling at VLDB scale).</summary>
    Task DeleteManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);

    Task<int> CountAllAsync(CancellationToken cancellationToken = default);

    Task<int> CountForJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IObjectStateRepository" />
public sealed class ObjectStateRepository : IObjectStateRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ObjectStateRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    private const string SelectColumns = """
        SELECT id AS Id, job_id AS JobId, database_name AS DatabaseName, object_type AS ObjectType,
               schema_name AS SchemaName, object_name AS ObjectName, object_id AS ObjectId, file_path AS FilePath,
               last_hash AS LastHash, last_scripted_at AS LastScriptedAt, last_committed_at AS LastCommittedAt,
               last_commit_sha AS LastCommitSha, last_run_id AS LastRunId, last_status AS LastStatus,
               error_message AS ErrorMessage
        FROM object_states
        """;

    public async Task<IReadOnlyList<TrackedObjectState>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // NOCASE database match: a re-typed database name that differs only by case must load the
        // same prior state, not orphan it (which would re-add everything and delete the old tree).
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(
            $"{SelectColumns} WHERE job_id = $job AND database_name = $db COLLATE NOCASE;",
            new { job = jobId.ToString(), db = database }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<TrackedObjectState>> GetTrackingStatesAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SlimStateRow>(new CommandDefinition(
            """
            SELECT id AS Id, object_type AS ObjectType, schema_name AS SchemaName,
                   object_name AS ObjectName, file_path AS FilePath, last_hash AS LastHash
            FROM object_states
            WHERE job_id = $job AND database_name = $db COLLATE NOCASE;
            """,
            new { job = jobId.ToString(), db = database }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // DatabaseName comes from the (NOCASE-matched) argument instead of one duplicated string
        // per row; the unset wide-row fields stay at their defaults — the engine never reads them.
        return [.. rows.Select(row => new TrackedObjectState
        {
            Id = row.Id,
            JobId = jobId,
            DatabaseName = database,
            ObjectType = (Shared.Objects.SqlObjectType)row.ObjectType,
            SchemaName = row.SchemaName,
            ObjectName = row.ObjectName,
            FilePath = row.FilePath,
            LastHash = row.LastHash,
        })];
    }

    public async Task<IReadOnlyList<TrackedObjectState>> SearchAsync(
        Guid jobId, string database, string query, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Escape LIKE wildcards typed by the user, then match name or schema.name. Types >= 60 are
        // Obsync's synthetic artifacts (inventory/reference data/server objects), not catalog objects.
        var escaped = query.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        // NOCASE database match, same as GetForJobDatabaseAsync: the identity index compares
        // database_name with NOCASE (V011), so a BINARY comparison here could not seek it past
        // job_id and would miss rows whose stored casing differs from the caller's.
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(
            $"""
            {SelectColumns}
            WHERE job_id = $job AND database_name = $db COLLATE NOCASE AND object_type < 60
              AND (object_name LIKE $pattern ESCAPE '\' OR (schema_name || '.' || object_name) LIKE $pattern ESCAPE '\')
            ORDER BY schema_name, object_name LIMIT $limit;
            """,
            new { job = jobId.ToString(), db = database, pattern = $"%{escaped}%", limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<string>> GetDatabasesForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var names = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT database_name FROM object_states
            WHERE job_id = $job AND object_type < 60 ORDER BY database_name;
            """,
            new { job = jobId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. names];
    }

    /// <summary>
    /// Rows per multi-row upsert statement: 14 parameters per row × 200 rows = 2,800 parameters,
    /// comfortably under the bundled e_sqlite3's 32,766-variable limit
    /// (SQLITE_LIMIT_VARIABLE_NUMBER, verified empirically by BatchInsertChunkingTests).
    /// </summary>
    internal const int UpsertChunkRows = 200;

    private static string BuildUpsertSql(int rowCount)
    {
        var values = string.Join(",\n    ", Enumerable.Range(0, rowCount).Select(i =>
            $"($job{i}, $db{i}, $type{i}, $schema{i}, $name{i}, $objectId{i}, $path{i}, $hash{i}, " +
            $"$scripted{i}, $committed{i}, $sha{i}, $run{i}, $status{i}, $error{i})"));
        return $"""
            INSERT INTO object_states
                (job_id, database_name, object_type, schema_name, object_name, object_id, file_path, last_hash,
                 last_scripted_at, last_committed_at, last_commit_sha, last_run_id, last_status, error_message)
            VALUES
                {values}
            ON CONFLICT (job_id, database_name, object_type, schema_name, object_name) DO UPDATE SET
                object_id = excluded.object_id, file_path = excluded.file_path, last_hash = excluded.last_hash,
                last_scripted_at = excluded.last_scripted_at, last_committed_at = excluded.last_committed_at,
                last_commit_sha = excluded.last_commit_sha, last_run_id = excluded.last_run_id,
                last_status = excluded.last_status, error_message = excluded.error_message,
                -- The identity index is NOCASE (V011): a case-only rename updates the existing row, and
                -- these keep the stored casing current with the live catalog.
                database_name = excluded.database_name, schema_name = excluded.schema_name,
                object_name = excluded.object_name;
            """;
    }

    private static readonly string SingleUpsertSql = BuildUpsertSql(1);
    private static readonly string FullChunkUpsertSql = BuildUpsertSql(UpsertChunkRows);

    private static void AddUpsertParameters(DynamicParameters parameters, TrackedObjectState state, int i)
    {
        parameters.Add($"job{i}", state.JobId.ToString());
        parameters.Add($"db{i}", state.DatabaseName);
        parameters.Add($"type{i}", (int)state.ObjectType);
        parameters.Add($"schema{i}", state.SchemaName);
        parameters.Add($"name{i}", state.ObjectName);
        parameters.Add($"objectId{i}", state.ObjectId);
        parameters.Add($"path{i}", state.FilePath);
        parameters.Add($"hash{i}", state.LastHash);
        parameters.Add($"scripted{i}", state.LastScriptedAt);
        parameters.Add($"committed{i}", state.LastCommittedAt);
        parameters.Add($"sha{i}", state.LastCommitSha);
        parameters.Add($"run{i}", state.LastRunId?.ToString());
        parameters.Add($"status{i}", (int)state.LastStatus);
        parameters.Add($"error{i}", state.ErrorMessage);
    }

    public async Task UpsertAsync(TrackedObjectState state, CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters();
        AddUpsertParameters(parameters, state, 0);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SingleUpsertSql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpsertManyAsync(
        IReadOnlyCollection<TrackedObjectState> states, CancellationToken cancellationToken = default)
    {
        if (states.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction for the whole batch (SQLite pays the journal fsync once instead of once
        // per row), and multi-row VALUES chunks so each command upserts UpsertChunkRows rows —
        // per-row commands cost a Dapper prepare + SQLite round-trip each, which dominates a
        // VLDB first run at hundreds of thousands of states.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in states.Chunk(UpsertChunkRows))
        {
            // Full chunks reuse one cached SQL text so Dapper caches a single command shape.
            var sql = chunk.Length == UpsertChunkRows ? FullChunkUpsertSql : BuildUpsertSql(chunk.Length);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Length; i++)
            {
                AddUpsertParameters(parameters, chunk[i], i);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM object_states WHERE id = $id;", new { id }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task DeleteManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Chunked IN lists keep each statement well under SQLite's parameter limit.
        foreach (var chunk in ids.Chunk(500))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM object_states WHERE id IN @ids;", new { ids = chunk },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM object_states;", cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM object_states WHERE job_id = $job;",
            new { job = jobId.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static TrackedObjectState Map(StateRow row) => new()
    {
        Id = row.Id,
        JobId = Guid.Parse(row.JobId),
        DatabaseName = row.DatabaseName,
        ObjectType = (Shared.Objects.SqlObjectType)row.ObjectType,
        SchemaName = row.SchemaName,
        ObjectName = row.ObjectName,
        ObjectId = (int?)row.ObjectId,
        FilePath = row.FilePath,
        LastHash = row.LastHash,
        LastScriptedAt = row.LastScriptedAt,
        LastCommittedAt = row.LastCommittedAt,
        LastCommitSha = row.LastCommitSha,
        LastRunId = string.IsNullOrEmpty(row.LastRunId) ? null : Guid.Parse(row.LastRunId),
        LastStatus = (Shared.RunStatus)row.LastStatus,
        ErrorMessage = row.ErrorMessage,
    };

    private sealed class SlimStateRow
    {
        public long Id { get; set; }
        public long ObjectType { get; set; }
        public string SchemaName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string LastHash { get; set; } = string.Empty;
    }

    private sealed class StateRow
    {
        public long Id { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public long ObjectType { get; set; }
        public string SchemaName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public long? ObjectId { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string LastHash { get; set; } = string.Empty;
        public DateTimeOffset LastScriptedAt { get; set; }
        public DateTimeOffset? LastCommittedAt { get; set; }
        public string? LastCommitSha { get; set; }
        public string? LastRunId { get; set; }
        public long LastStatus { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
