namespace Obsync.Shared;

/// <summary>
/// A machine-wide, per-job execution lock so the same job never runs twice at once — no matter which
/// host starts it (desktop app, Windows service, or CLI). Implemented as an exclusively-opened lock
/// file: the OS releases the handle when the owning process exits (including a crash), so a stale
/// lock can never outlive its process. All hosts share the same lock directory because they share
/// the same per-user data root; a host running as a different account normally has a different data
/// root AND a different database, so there is no shared work to protect across accounts — the
/// exception is a machine-wide <c>OBSYNC_DATA_ROOT</c>, which makes the root common while leaving
/// the lock files owned by whichever account created them.
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
    /// <summary>How long to keep retrying an ambiguous access denial before treating it as real.</summary>
    private const int DeniedRetries = 5;

    private static readonly TimeSpan DeniedRetryDelay = TimeSpan.FromMilliseconds(20);

    public static IDisposable? TryAcquire(string locksRoot, string name)
    {
        Directory.CreateDirectory(locksRoot);
        var path = Path.Combine(locksRoot, $"{name}.lock");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // FileShare.None is the lock; DeleteOnClose keeps the directory clean.
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return null; // sharing violation: genuinely held by another process (or thread)
            }
            catch (UnauthorizedAccessException) when (attempt < DeniedRetries)
            {
                // A DeleteOnClose deletion is performed during handle cleanup, and an open arriving
                // in that window is refused with STATUS_DELETE_PENDING — which Win32 reports as the
                // SAME ERROR_ACCESS_DENIED as a real ACL denial, so the two are indistinguishable
                // by error code (only the raw NTSTATUS separates them). Time is the practical
                // discriminator: a pending delete clears in milliseconds, an ACL problem never
                // does. This costs nothing on the genuinely-held path, which returns above.
                Thread.Sleep(DeniedRetryDelay);
            }
        }

        // A denial that outlives the retries is NOT contention, so it is deliberately allowed to
        // escape rather than being reported as null. Returning null told every caller "another
        // process holds this", which sent users hunting for a run that does not exist while every
        // occurrence skipped forever: a read-only leftover .lock file (these survive power loss,
        // and restore-from-backup re-applies the attribute) or a deny ACE on the locks folder.
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
        // Read-only, and deliberately NOT via TryAcquire. Probing by taking the lock creates the
        // file, holds it exclusively, and deletes it on close — so the probe could lose a genuine
        // acquirer its race (the app runs this over every "Running" row at startup, while the
        // service may be starting those very jobs) and leave a delete-pending window behind it.
        // FileMode.Open never creates, no DeleteOnClose never removes, and the widest sharing means
        // a live holder's FileShare.None is the only thing that can refuse us.
        try
        {
            using var probe = new FileStream(
                Path.Combine(locksRoot, $"job-{jobId:N}.lock"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
            return false; // a leftover nobody holds — the next real acquire reclaims it
        }
        // Both of these derive from IOException, so they MUST be caught first or a job that has
        // never run reports as held.
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // sharing violation: a live holder
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot verify. Answering "held" is the conservative direction here: it leaves an
            // orphaned row alone rather than failing a run that may be alive, and it keeps this
            // probe from throwing into app startup — the run path reports the denial loudly.
            return true;
        }
    }
}
