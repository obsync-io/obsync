namespace Obsync.Shared.Models;

/// <summary>
/// An append-only record of a user-attributable action, for the enterprise audit trail. Stored in
/// the local state database; never contains secrets.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Who performed the action, as <c>DOMAIN\user</c>.</summary>
    public string Actor { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    /// <summary>The kind of entity acted on, e.g. "Job", "Server", "Repository".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The entity's identifier (a Guid string), when applicable.</summary>
    public string? EntityId { get; set; }

    /// <summary>The entity's friendly name at the time of the action, when known.</summary>
    public string? EntityName { get; set; }

    /// <summary>Optional extra context (e.g. the run trigger).</summary>
    public string? Detail { get; set; }
}
