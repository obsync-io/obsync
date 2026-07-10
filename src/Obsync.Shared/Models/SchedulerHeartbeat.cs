namespace Obsync.Shared.Models;

/// <summary>
/// A liveness beacon the scheduler service writes into the shared database every reconcile tick.
/// The app uses it to answer the one question the Service Control Manager cannot: "is a scheduler
/// running against MY jobs?" — a service running under a different account heartbeats into that
/// account's database, so a fresh heartbeat here proves both liveness and data visibility.
/// </summary>
public sealed class SchedulerHeartbeat
{
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>The Windows account the scheduler runs as, e.g. <c>DOMAIN\user</c>.</summary>
    public string Account { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}
