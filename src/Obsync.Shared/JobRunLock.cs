namespace Obsync.Shared;

/// <summary>
/// A machine-wide, per-job execution lock so the same job never runs twice at once — no matter which
/// host starts it (desktop app, Windows service, or CLI). Implemented as an exclusively-opened lock
/// file: the OS releases the handle when the owning process exits (including a crash), so a stale
/// lock can never outlive its process. All hosts share the same lock directory because they share
/// the same per-user data root; a host running as a different account has a different data root AND
/// a different database, so there is no shared work to protect across accounts.
/// </summary>
public static class JobRunLock
{
    /// <summary>
    /// Tries to take the exclusive run lock for a job. Returns a handle to dispose when the run is
    /// fully finished (including final persistence), or null when another process already holds it.
    /// </summary>
    public static IDisposable? TryAcquire(string locksRoot, Guid jobId) =>
        TryAcquire(locksRoot, $"job-{jobId:N}");

    /// <summary>
    /// Tries to take an arbitrary named machine-wide lock under the same crash-safe scheme —
    /// e.g. the per-repository workspace lock (<c>repo-{id}</c>) that keeps two different jobs
    /// sharing one clone from interleaving git operations.
    /// </summary>
    public static IDisposable? TryAcquire(string locksRoot, string name)
    {
        Directory.CreateDirectory(locksRoot);
        try
        {
            // FileShare.None is the lock; DeleteOnClose keeps the directory clean without a race
            // (the delete happens atomically with the handle close, so a waiter that opens the file
            // a moment earlier still holds a valid lock on the same path).
            return new FileStream(
                Path.Combine(locksRoot, $"{name}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null; // held by another process (or another thread in this one)
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits (polling) for a named lock to become free, up to <paramref name="timeout"/>. Returns
    /// the held lock, or null when the timeout elapsed. Used where skipping would silently drop
    /// work — e.g. two jobs sharing a repository run back-to-back instead of one being skipped.
    /// </summary>
    public static async Task<IDisposable?> WaitAsync(
        string locksRoot, string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var handle = TryAcquire(locksRoot, name);
            if (handle is not null || DateTime.UtcNow >= deadline)
            {
                return handle;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when some live process currently holds the run lock for this job. Used to tell a run
    /// that is genuinely in progress (in any host) apart from an orphaned "Running" database row
    /// left behind by a crash.
    /// </summary>
    public static bool IsHeld(string locksRoot, Guid jobId)
    {
        using var probe = TryAcquire(locksRoot, jobId);
        return probe is null;
    }
}
