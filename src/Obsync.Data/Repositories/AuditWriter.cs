using Dapper;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <inheritdoc cref="IAuditWriter" />
public sealed class AuditWriter : IAuditWriter
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public AuditWriter(IDbConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task WriteAsync(
        AuditAction action,
        string entityType,
        string? entityId,
        string? entityName,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (occurred_at, actor, action, entity_type, entity_id, entity_name, detail)
            VALUES ($occurred, $actor, $action, $entityType, $entityId, $entityName, $detail);
            """,
            new
            {
                occurred = _clock.UtcNow,
                actor = CurrentActor.Name,
                action = action.ToString(),
                entityType,
                entityId,
                entityName,
                detail,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT id AS Id, occurred_at AS OccurredAt, actor AS Actor, action AS Action,
                   entity_type AS EntityType, entity_id AS EntityId, entity_name AS EntityName, detail AS Detail
            FROM audit_log ORDER BY id DESC LIMIT $limit;
            """,
            new { limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => new AuditEvent
        {
            Id = r.Id,
            OccurredAt = r.OccurredAt,
            Actor = r.Actor,
            // Stored as the enum name; tolerate an unknown value rather than throwing on read.
            Action = Enum.TryParse<AuditAction>(r.Action, out var a) ? a : default,
            EntityType = r.EntityType,
            EntityId = r.EntityId,
            EntityName = r.EntityName,
            Detail = r.Detail,
        })];
    }

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string Actor { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string? EntityId { get; set; }
        public string? EntityName { get; set; }
        public string? Detail { get; set; }
    }
}
