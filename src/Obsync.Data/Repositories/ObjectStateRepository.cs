using Dapper;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for per-object tracking state (hashes and commit metadata).</summary>
public interface IObjectStateRepository
{
    Task<IReadOnlyList<TrackedObjectState>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default);

    Task UpsertAsync(TrackedObjectState state, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

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
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(
            $"{SelectColumns} WHERE job_id = $job AND database_name = $db;",
            new { job = jobId.ToString(), db = database }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task UpsertAsync(TrackedObjectState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO object_states
                (job_id, database_name, object_type, schema_name, object_name, object_id, file_path, last_hash,
                 last_scripted_at, last_committed_at, last_commit_sha, last_run_id, last_status, error_message)
            VALUES
                ($job, $db, $type, $schema, $name, $objectId, $path, $hash, $scripted, $committed, $sha, $run, $status, $error)
            ON CONFLICT (job_id, database_name, object_type, schema_name, object_name) DO UPDATE SET
                object_id = excluded.object_id, file_path = excluded.file_path, last_hash = excluded.last_hash,
                last_scripted_at = excluded.last_scripted_at, last_committed_at = excluded.last_committed_at,
                last_commit_sha = excluded.last_commit_sha, last_run_id = excluded.last_run_id,
                last_status = excluded.last_status, error_message = excluded.error_message;
            """,
            new
            {
                job = state.JobId.ToString(),
                db = state.DatabaseName,
                type = (int)state.ObjectType,
                schema = state.SchemaName,
                name = state.ObjectName,
                objectId = state.ObjectId,
                path = state.FilePath,
                hash = state.LastHash,
                scripted = state.LastScriptedAt,
                committed = state.LastCommittedAt,
                sha = state.LastCommitSha,
                run = state.LastRunId?.ToString(),
                status = (int)state.LastStatus,
                error = state.ErrorMessage,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM object_states WHERE id = $id;", new { id }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
