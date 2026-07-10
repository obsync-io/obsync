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
}
