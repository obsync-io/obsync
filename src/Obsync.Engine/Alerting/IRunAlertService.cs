using Obsync.Shared.Models;
using Obsync.Shared.Results;

namespace Obsync.Engine.Alerting;

/// <summary>
/// Best-effort run alerting over the globally configured channels (SMTP email + webhook).
/// Delivery failures are logged and swallowed — an alert must never fail or delay a run.
/// </summary>
public interface IRunAlertService
{
    /// <summary>
    /// Evaluates the global alert settings against a finished, persisted run and sends each
    /// enabled channel independently (one channel failing does not stop the other).
    /// </summary>
    Task NotifyAsync(SyncRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a test alert through every enabled channel (for the Settings "Send test alert"
    /// button), reporting the first delivery error.
    /// </summary>
    Task<Result> SendTestAsync(CancellationToken cancellationToken);
}
