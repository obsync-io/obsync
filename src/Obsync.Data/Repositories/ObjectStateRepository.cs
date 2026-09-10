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
    Task<IReadOnlyList<TrackedObjectSnapshot>> GetTrackingStatesAsync(
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
    /// Upserts a batch on ONE connection, in bounded transactions. On a VLDB first run this persists
    /// hundreds of thousands of states — per-row connections/auto-commits would take longer than
    /// the scripting itself, and one transaction for the whole batch would hold the database's
    /// single write lock against every other writer for its entire duration.
    /// </summary>
    Task UpsertManyAsync(IReadOnlyCollection<TrackedObjectState> states, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a batch of state rows (mass-drop handling at VLDB scale).</summary>
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

    public async Task<IReadOnlyList<TrackedObjectSnapshot>> GetTrackingStatesAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default)
    {
        // Read directly, rather than through Dapper, for the one query whose result set is the size
        // of the estate.
        //
        // Dapper buffers a whole result set by default, so a million SlimStateRow shells were alive
        // at once WHILE the million-element projection was being built — roughly double the
        // steady-state footprint, at exactly the moment the engine is about to allocate its own
        // per-object structures. Its unbuffered mode is not a safe substitute here (it was tried:
        // it fails this repository's own integration tests), so this reads the rows itself and
        // projects each one as it arrives. No intermediate row type exists at all now.
        //
        // The projection is the slim TrackedObjectSnapshot rather than the full persisted row: a run
        // reads exactly these six columns, and the other nine cost roughly 110 bytes per object in
        // fields nothing looks at.
        //
        // Schema names are pooled. A database of a million objects typically has a handful of
        // schemas, and a reader hands back a fresh string per row — so without this, "dbo" is
        // materialised a million times.
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, object_type, schema_name, object_name, file_path, last_hash
            FROM object_states
            WHERE job_id = $job AND database_name = $db COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString());
        command.Parameters.AddWithValue("$db", database);

        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new List<TrackedObjectSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(2);
            if (!schemas.TryGetValue(schema, out var pooledSchema))
            {
                pooledSchema = schema;
                schemas[schema] = pooledSchema;
            }

            states.Add(new TrackedObjectSnapshot
            {
                Id = reader.GetInt64(0),
                ObjectType = (Shared.Objects.SqlObjectType)reader.GetInt32(1),
                SchemaName = pooledSchema,
                ObjectName = reader.GetString(3),
                FilePath = reader.GetString(4),
                LastHash = reader.GetString(5),
            });
        }

        return states;
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

    /// <summary>
    /// Ids per <c>DELETE ... WHERE id IN (...)</c> statement: one parameter each, so 500 is far
    /// under the same 32,766-variable limit and keeps the expanded SQL text small.
    /// </summary>
    internal const int DeleteChunkIds = 500;

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

        // Multi-row VALUES chunks so each command upserts UpsertChunkRows rows — per-row commands
        // cost a Dapper prepare + SQLite round-trip each, which dominates a VLDB first run at
        // hundreds of thousands of states — inside BOUNDED transactions rather than one transaction
        // spanning the batch. See BoundedBatchWriter for why the write lock has to be released
        // periodically and how the row count was chosen.
        //
        // What an interrupted batch leaves behind, and why the next run repairs it:
        //
        //  * SyncEngine.PersistStatesAsync only calls this once the changeset has been DELIVERED
        //    (commit pushed, or the pull request opened). Every hash written here therefore
        //    describes content the repository ALREADY holds — a row in a committed segment is true,
        //    a row that did not make it is merely absent or stale.
        //
        //  * The rows are independent: one per tracked object, keyed by identity. Nothing reads two
        //    of them together and their order carries no meaning, so a prefix is as valid as the
        //    whole.
        //
        //  * Watermarks are written AFTER this call (and after DeleteManyAsync), so an interrupted
        //    batch can never leave a watermark ahead of the states it was meant to cover. That
        //    ordering is what makes bounding safe here: a watermark is the ONLY thing that lets a
        //    later run SKIP an object, and partial persistence therefore leaves the database behind
        //    the repository, never ahead of it. No object is ever treated as unchanged when it is.
        //
        //  * So the next run re-scripts the objects whose rows were not written, finds a hash that
        //    differs from (or has no) stored state, and records them as Modified/Added. It rewrites
        //    the files with byte-identical content, git stages nothing, and CommitAndPushAsync's
        //    "detected changes produced an identical git tree" branch marks the changeset delivered
        //    — so PersistStatesAsync runs and finishes the write. It converges in one run.
        //
        //  * A row that was never written cannot cause a spurious deletion either: the deletion pass
        //    only proposes deletions for objects that HAVE a state row.
        //
        // PersistStatesAsync is already a sequence of separate transactions (states, deletes,
        // watermarks, quarantine — each on its own connection), so this introduces no new failure
        // class. It makes the existing one finer-grained, and every extra checkpoint leaves LESS for
        // the next run to redo.
        await BoundedBatchWriter.ExecuteAsync(connection, states, UpsertChunkRows, chunk =>
        {
            // Full chunks reuse one cached SQL text so Dapper caches a single command shape.
            var sql = chunk.Length == UpsertChunkRows ? FullChunkUpsertSql : BuildUpsertSql(chunk.Length);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Length; i++)
            {
                AddUpsertParameters(parameters, chunk[i], i);
            }

            return (sql, parameters);
        }, cancellationToken).ConfigureAwait(false);
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

        // Bounded transactions, for the same reason as UpsertManyAsync — a mass drop hands this the
        // whole tracked population at once.
        //
        // The ids are independent, and a row that survives an interrupted batch is a tombstone for
        // an object that is gone from SQL and whose file the delivered commit has already removed.
        // The next run lists it as a deletion candidate again, DeleteRecordedFile finds the file
        // already absent and skips it, git stages nothing, the "identical git tree" branch delivers
        // the run, and the row is deleted for good. Committing partway is strictly BETTER than
        // all-or-nothing here: fewer leftover candidates next run means the mass-deletion circuit
        // breaker is less likely to read them as a suspicious disappearance and suspend them again.
        await BoundedBatchWriter.ExecuteAsync(
            connection, ids, DeleteChunkIds,
            // Chunked IN lists keep each statement well under SQLite's parameter limit.
            chunk => ("DELETE FROM object_states WHERE id IN @ids;", new { ids = chunk }),
            cancellationToken).ConfigureAwait(false);
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
