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
    public static IDisposable? TryAcquire(string locksRoot, Guid jobId)
    {
        Directory.CreateDirectory(locksRoot);
        try
        {
            // FileShare.None is the lock; DeleteOnClose keeps the directory clean without a race
            // (the delete happens atomically with the handle close, so a waiter that opens the file
            // a moment earlier still holds a valid lock on the same path).
            return new FileStream(
                LockPath(locksRoot, jobId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
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
    /// True when some live process currently holds the run lock for this job. Used to tell a run
    /// that is genuinely in progress (in any host) apart from an orphaned "Running" database row
    /// left behind by a crash.
    /// </summary>
    public static bool IsHeld(string locksRoot, Guid jobId)
    {
        using var probe = TryAcquire(locksRoot, jobId);
        return probe is null;
    }

    private static string LockPath(string locksRoot, Guid jobId) =>
        Path.Combine(locksRoot, $"job-{jobId:N}.lock");
}
