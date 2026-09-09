using System.Globalization;
using Dapper;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Data.Repositories;

/// <summary>
/// Persistence for quarantined objects: the ones Obsync has observed twice, under identical
/// evidence, that it cannot script. Rows cascade away with the owning job.
/// </summary>
public interface IScriptingQuarantineRepository
{
    /// <summary>Every quarantined object for one job and database, keyed by its state key.</summary>
    Task<IReadOnlyDictionary<string, QuarantinedObject>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default);

    /// <summary>Records or refreshes quarantine observations in a single transaction.</summary>
    Task UpsertManyAsync(
        Guid jobId, string database, IReadOnlyCollection<QuarantinedObject> entries,
        CancellationToken cancellationToken = default);

    /// <summary>Removes quarantine rows for objects that scripted successfully again.</summary>
    Task DeleteManyAsync(
        Guid jobId, string database, IReadOnlyCollection<ScriptedObjectIdentity> identities,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IScriptingQuarantineRepository" />
public sealed class ScriptingQuarantineRepository : IScriptingQuarantineRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ScriptingQuarantineRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<IReadOnlyDictionary<string, QuarantinedObject>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(
            long ObjectType, string SchemaName, string ObjectName, string ModifyDate,
            string Reason, string FirstSeenAt, string LastSeenAt)>(
            new CommandDefinition(
                "SELECT object_type, schema_name, object_name, modify_date, reason, first_seen_at, last_seen_at "
                + "FROM scripting_quarantine WHERE job_id = $job AND database_name = $db;",
                new { job = jobId.ToString(), db = database },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(
            r => $"{r.ObjectType}|{r.SchemaName}|{r.ObjectName}",
            r => new QuarantinedObject(
                new ScriptedObjectIdentity((SqlObjectType)r.ObjectType, r.SchemaName, r.ObjectName),
                DateTime.Parse(r.ModifyDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.Reason,
                DateTimeOffset.Parse(r.FirstSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(r.LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpsertManyAsync(
        Guid jobId, string database, IReadOnlyCollection<QuarantinedObject> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        // first_seen_at is preserved on conflict: it answers "how long has this been broken", which
        // is the only question an operator actually asks about a quarantined object.
        const string sql =
            """
            INSERT INTO scripting_quarantine
                (job_id, database_name, object_type, schema_name, object_name,
                 modify_date, reason, first_seen_at, last_seen_at)
            VALUES ($job, $db, $type, $schema, $name, $modifyDate, $reason, $seenAt, $seenAt)
            ON CONFLICT (job_id, database_name, object_type, schema_name, object_name) DO UPDATE SET
                modify_date  = excluded.modify_date,
                reason       = excluded.reason,
                last_seen_at = excluded.last_seen_at;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                job = jobId.ToString(),
                db = database,
                type = (int)entry.Identity.Type,
                schema = entry.Identity.Schema,
                name = entry.Identity.Name,
                // "O" round-trips the raw DateTime exactly, preserving the opaque server-local value.
                modifyDate = entry.ModifyDate.ToString("O", CultureInfo.InvariantCulture),
                reason = entry.Reason,
                seenAt = entry.LastSeenAt.ToString("O", CultureInfo.InvariantCulture),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteManyAsync(
        Guid jobId, string database, IReadOnlyCollection<ScriptedObjectIdentity> identities,
        CancellationToken cancellationToken = default)
    {
        if (identities.Count == 0)
        {
            return;
        }

        const string sql =
            """
            DELETE FROM scripting_quarantine
            WHERE job_id = $job AND database_name = $db
              AND object_type = $type AND schema_name = $schema AND object_name = $name;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var identity in identities)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                job = jobId.ToString(),
                db = database,
                type = (int)identity.Type,
                schema = identity.Schema,
                name = identity.Name,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
