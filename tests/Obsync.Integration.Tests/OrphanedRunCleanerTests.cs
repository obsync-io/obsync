using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Integration.Tests;

/// <summary>
/// Crash-recovery contract against a real database: a "Running" row whose job lock is free is an
/// orphan and is failed with a reason; a row whose lock is held (a live run in some process) is
/// left alone — this is what protects a long service run from the app's startup cleanup.
/// </summary>
public sealed class OrphanedRunCleanerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-test-{Guid.NewGuid():N}.db");
    private readonly string _locksRoot = Path.Combine(Path.GetTempPath(), $"obsync-locks-{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Persists a job (runs carry a foreign key to it) and returns a Running run for it.</summary>
    private async Task<SyncRun> RunningRunAsync()
    {
        var connection = new SqlConnectionProfile { Name = $"s-{Guid.NewGuid():N}", ServerName = "s" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);

        var job = new SyncJob
        {
            Name = $"j-{Guid.NewGuid():N}",
            ConnectionProfileId = connection.Id,
            CommitMode = CommitMode.ExportOnly, // no repository profile needed
            ExportPath = @"C:\export",
            Databases = ["db"],
        };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(job);

        return new SyncRun
        {
            RunKey = "20260710-080000",
            JobId = job.Id,
            JobName = job.Name,
            Trigger = RunTrigger.Scheduled,
            Status = RunStatus.Running,
            ServerName = "s",
            Databases = "db",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
    }

    [Fact]
    public async Task RunningRow_WithNoLockHolder_IsFailedWithAReason()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await RunningRunAsync();
        await runs.InsertAsync(run);

        var cleaned = await OrphanedRunCleaner.CleanAsync(runs, _locksRoot, DateTimeOffset.UtcNow);

        Assert.Equal(1, cleaned);
        var reloaded = await runs.GetAsync(run.Id);
        Assert.Equal(RunStatus.Failed, reloaded!.Status);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.Contains("interrupted", reloaded.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunningRow_WhoseLockIsHeld_IsLeftAlone()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await RunningRunAsync();
        await runs.InsertAsync(run);

        using var liveRun = JobRunLock.TryAcquire(_locksRoot, run.JobId);
        Assert.NotNull(liveRun);

        var cleaned = await OrphanedRunCleaner.CleanAsync(runs, _locksRoot, DateTimeOffset.UtcNow);

        Assert.Equal(0, cleaned);
        var reloaded = await runs.GetAsync(run.Id);
        Assert.Equal(RunStatus.Running, reloaded!.Status);
    }

    [Fact]
    public async Task CompletedRows_AreNeverTouched()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await RunningRunAsync();
        run.Status = RunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await runs.InsertAsync(run);

        var cleaned = await OrphanedRunCleaner.CleanAsync(runs, _locksRoot, DateTimeOffset.UtcNow);

        Assert.Equal(0, cleaned);
        Assert.Equal(RunStatus.Succeeded, (await runs.GetAsync(run.Id))!.Status);
    }
}
