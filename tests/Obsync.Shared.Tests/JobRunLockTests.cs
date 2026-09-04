using Obsync.Shared;

namespace Obsync.Shared.Tests;

public sealed class JobRunLockTests : IDisposable
{
    private readonly string _locksRoot = Path.Combine(Path.GetTempPath(), $"obsync-locks-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_locksRoot, recursive: true);
        }
        catch (IOException)
        {
            // A lock file may still be open if a test failed mid-assert; temp cleanup is best-effort.
        }
    }

    [Fact]
    public void SecondAcquire_IsRefused_WhileTheFirstIsHeld()
    {
        var jobId = Guid.NewGuid();

        using var first = JobRunLock.TryAcquire(_locksRoot, jobId);
        Assert.NotNull(first);
        Assert.Null(JobRunLock.TryAcquire(_locksRoot, jobId));
    }

    [Fact]
    public void Acquire_SucceedsAgain_AfterRelease()
    {
        var jobId = Guid.NewGuid();

        var first = JobRunLock.TryAcquire(_locksRoot, jobId);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = JobRunLock.TryAcquire(_locksRoot, jobId);
        Assert.NotNull(second);
    }

    [Fact]
    public void DifferentJobs_DoNotContend()
    {
        using var first = JobRunLock.TryAcquire(_locksRoot, Guid.NewGuid());
        using var second = JobRunLock.TryAcquire(_locksRoot, Guid.NewGuid());

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void NamedLock_SecondAcquire_IsRefused_WhileHeld()
    {
        using var first = JobRunLock.TryAcquire(_locksRoot, "repo-abc");
        Assert.NotNull(first);
        Assert.Null(JobRunLock.TryAcquire(_locksRoot, "repo-abc"));
        Assert.NotNull(JobRunLock.TryAcquire(_locksRoot, "repo-other"));
    }

    [Fact]
    public async Task WaitAsync_AcquiresTheLock_OnceTheHolderReleases()
    {
        var holder = JobRunLock.TryAcquire(_locksRoot, "repo-wait");
        Assert.NotNull(holder);

        var waiter = JobRunLock.WaitAsync(_locksRoot, "repo-wait", TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        holder!.Dispose();

        using var acquired = await waiter;
        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task WaitAsync_ReturnsNull_WhenTheTimeoutElapses()
    {
        using var holder = JobRunLock.TryAcquire(_locksRoot, "repo-busy");
        Assert.NotNull(holder);

        Assert.Null(await JobRunLock.WaitAsync(_locksRoot, "repo-busy", TimeSpan.Zero));
    }

    [Fact]
    public void IsHeld_TracksTheLockLifetime_AndDoesNotStealIt()
    {
        var jobId = Guid.NewGuid();
        Assert.False(JobRunLock.IsHeld(_locksRoot, jobId));

        using (JobRunLock.TryAcquire(_locksRoot, jobId))
        {
            Assert.True(JobRunLock.IsHeld(_locksRoot, jobId));
            Assert.True(JobRunLock.IsHeld(_locksRoot, jobId)); // probing must not release the real lock
        }

        Assert.False(JobRunLock.IsHeld(_locksRoot, jobId));
    }

    [Fact]
    public async Task ProbingDoesNotStealTheLockFromAConcurrentAcquirer()
    {
        // IsHeld used to probe by TAKING the lock — creating the file, holding it exclusively and
        // deleting it on close. That could lose a genuine acquirer its race: the app probes every
        // "Running" row at startup while the service may be starting those very jobs, and the loser
        // persists a Skipped occurrence blaming a run that does not exist.
        var jobId = Guid.NewGuid();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var prober = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                JobRunLock.IsHeld(_locksRoot, jobId);
            }
        });

        var refusals = 0;
        var attempts = 0;
        while (!stop.IsCancellationRequested)
        {
            attempts++;
            using var held = JobRunLock.TryAcquire(_locksRoot, jobId);
            if (held is null)
            {
                refusals++;
            }
        }

        await prober;
        Assert.True(attempts > 0);
        Assert.Equal(0, refusals);
    }

    [Fact]
    public void ReacquiringImmediatelyAfterRelease_AlwaysSucceeds()
    {
        // DeleteOnClose performs the deletion during handle cleanup, and an open arriving in that
        // window is refused with STATUS_DELETE_PENDING — surfaced as the same ERROR_ACCESS_DENIED
        // as a real ACL denial. Back-to-back occurrences of one job hit exactly this.
        var jobId = Guid.NewGuid();
        for (var i = 0; i < 500; i++)
        {
            var held = JobRunLock.TryAcquire(_locksRoot, jobId);
            Assert.NotNull(held);
            held.Dispose();
        }
    }

    [Fact]
    public void ADeniedLock_SurfacesAsAnError_RatherThanMasqueradingAsContention()
    {
        // A read-only leftover .lock file survives power loss, and restore-from-backup re-applies
        // the attribute. Reporting that as "held" skipped every occurrence forever while blaming
        // another process; IsHeld still answers conservatively so app startup cannot throw.
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(_locksRoot);
        var path = Path.Combine(_locksRoot, $"job-{jobId:N}.lock");
        File.WriteAllText(path, string.Empty);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            // Acquiring needs write access, so it is refused — loudly, which is the point.
            Assert.Throws<UnauthorizedAccessException>(() => JobRunLock.TryAcquire(_locksRoot, jobId));

            // IsHeld only needs READ, which a read-only file still permits, so it correctly answers
            // "nobody holds this" — the run really is orphaned and crash recovery should fail it.
            // (A deny-ACE that blocks reading too takes the conservative branch and answers true.)
            Assert.False(JobRunLock.IsHeld(_locksRoot, jobId));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
