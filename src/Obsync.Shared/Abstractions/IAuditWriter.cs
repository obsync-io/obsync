using Obsync.Shared.Models;

namespace Obsync.Shared.Abstractions;

/// <summary>
/// Writes and reads the enterprise audit trail — an append-only record of who did what. Stored in
/// the local state database (never contains secrets). The actor and timestamp are captured by the
/// implementation, so callers pass only the action and its target.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Records an audited action performed by the current identity, now.</summary>
    Task WriteAsync(
        AuditAction action,
        string entityType,
        string? entityId,
        string? entityName,
        string? detail = null,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent audit events, newest first.</summary>
    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
}
