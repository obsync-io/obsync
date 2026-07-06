using System.Globalization;
using Dapper;
using Obsync.Shared.Objects;

namespace Obsync.Data.Repositories;

/// <summary>
/// Persistence for incremental-scripting watermarks: the max <c>sys.objects.modify_date</c> seen
/// per job/database/object type on the last successful run. Values are opaque server-local
/// datetimes stored verbatim (never converted between time zones). Rows cascade away with the
/// owning job via the <c>scripting_watermarks.job_id</c> foreign key.
/// </summary>
public interface IScriptingWatermarkRepository
{
    Task<IReadOnlyDictionary<SqlObjectType, DateTime>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default);

    /// <summary>Upserts one database's watermarks in a single transaction.</summary>
    Task UpsertManyAsync(
        Guid jobId, string database, IReadOnlyDictionary<SqlObjectType, DateTime> watermarks,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IScriptingWatermarkRepository" />
public sealed class ScriptingWatermarkRepository : IScriptingWatermarkRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ScriptingWatermarkRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyDictionary<SqlObjectType, DateTime>> GetForJobDatabaseAsync(
        Guid jobId, string database, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(long ObjectType, string Watermark)>(new CommandDefinition(
            "SELECT object_type, watermark FROM scripting_watermarks WHERE job_id = $job AND database_name = $db;",
            new { job = jobId.ToString(), db = database }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(
            r => (SqlObjectType)r.ObjectType,
            r => DateTime.Parse(r.Watermark, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task UpsertManyAsync(
        Guid jobId, string database, IReadOnlyDictionary<SqlObjectType, DateTime> watermarks,
        CancellationToken cancellationToken = default)
    {
        if (watermarks.Count == 0)
        {
            return;
        }

        const string sql =
            """
            INSERT INTO scripting_watermarks (job_id, database_name, object_type, watermark)
            VALUES ($job, $db, $type, $watermark)
            ON CONFLICT (job_id, database_name, object_type) DO UPDATE SET watermark = excluded.watermark;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (type, watermark) in watermarks)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                job = jobId.ToString(),
                db = database,
                type = (int)type,
                // "O" round-trips the raw DateTime exactly, preserving the opaque server-local value.
                watermark = watermark.ToString("O", CultureInfo.InvariantCulture),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
