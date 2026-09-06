using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Obsync.Service;

/// <summary>
/// Registers the service host's background workers.
/// </summary>
/// <remarks>
/// Extracted from <c>Program.cs</c> so the order below is assertable, because the order IS the
/// behavior. <see cref="Microsoft.Extensions.Hosting.IHostedService"/> instances start in
/// registration order and stop in REVERSE registration order, and the whole shutdown budget
/// (<c>HostOptions.ShutdownTimeout</c>) is shared between all of them.
/// </remarks>
public static class ObsyncHostedServices
{
    /// <summary>Adds the hosted services in the order shutdown correctness depends on.</summary>
    public static IServiceCollection AddObsyncHostedServices(this IServiceCollection services)
    {
        // FIRST registered => LAST to stop. Quartz's WaitForJobsToComplete only waits; it never
        // signals cancellation, so it has to run after something has actually asked the jobs to
        // stop, or it just burns the remaining budget watching a run it never interrupted.
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        services.AddHostedService<JobSchedulingBootstrapper>();
        services.AddHostedService<JobReconciliationService>();
        services.AddHostedService<RunRetentionService>();

        // LAST registered => FIRST to stop, which is the point.
        //
        // This used to sit directly after the Quartz host, which satisfied "cancel before Quartz
        // waits" but missed that a LATER-registered service stops EARLIER. JobSchedulingBootstrapper
        // is a plain IHostedService whose StopAsync does two synchronous database writes (clear the
        // heartbeat, write a lifecycle audit event), each able to block for the 30s SQLite busy
        // timeout — and the writer most likely to be holding that lock is the very run we have not
        // asked to stop yet. Worst case burned 60s of a 90s budget BEFORE the first interrupt was
        // issued, leaving Quartz ~10s to wait for a run that had only just been told to cancel.
        //
        // Cancelling first also makes those two writes fast, because the run releases the write lock
        // on its way out.
        services.AddHostedService<RunCancellationOnStopService>();

        return services;
    }
}
