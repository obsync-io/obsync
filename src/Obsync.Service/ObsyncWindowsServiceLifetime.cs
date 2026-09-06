using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Obsync.Service;

/// <summary>Swaps the framework's Windows service lifetime for ours.</summary>
internal static class ObsyncWindowsServiceLifetimeRegistration
{
    /// <summary>
    /// Replaces the <see cref="IHostLifetime"/> that <c>AddWindowsService</c> registered.
    /// </summary>
    /// <remarks>
    /// A separate method purely so the replacement is assertable: registering ours *alongside* the
    /// framework's would leave the last one registered winning, which is the kind of thing that
    /// works on one runtime version and quietly stops working on the next.
    /// </remarks>
    public static IServiceCollection UseObsyncWindowsServiceLifetime(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IHostLifetime, ObsyncWindowsServiceLifetime>());
        return services;
    }
}

/// <summary>
/// The Windows service lifetime, with two corrections to the framework's stop behavior that an
/// MSI upgrade depends on.
/// </summary>
/// <remarks>
/// Both defects below were found by decoding the shipped framework assemblies, and both matter
/// specifically because an upgrade STOPS THIS SERVICE AND THEN IMMEDIATELY OVERWRITES ITS FILES.
/// The MSI's <c>&lt;ServiceControl Stop="both" Wait="yes"&gt;</c> is only as good as what the
/// service tells the SCM.
///
/// <para>
/// <b>1. The framework reports a stop it has not achieved.</b>
/// <see cref="WindowsServiceLifetime"/>'s <c>OnStop</c> is
/// <c>StopApplication(); _delayStop.Wait(HostOptions.ShutdownTimeout); base.OnStop();</c> — and
/// nothing anywhere kills the process. When the budget expires it returns REGARDLESS,
/// <see cref="System.ServiceProcess.ServiceBase"/> reports SERVICE_STOPPED, and MSI's Wait="yes"
/// is satisfied while the process is still alive, still running a sync, and still holding every
/// DLL in the install folder. MSI then copies over a live process (reboot-deferred replacement of
/// whatever it holds) and starts a second service alongside the first. A hung drain that is killed
/// is recoverable; one that is declared finished is not.
/// </para>
///
/// <para>
/// <b>2. The framework asks for zero time.</b> <c>ServiceBase.DeferredStop</c> writes
/// <c>checkPoint = 0; waitHint = 0; currentState = SERVICE_STOP_PENDING</c> before invoking
/// <c>OnStop</c>, and nothing calls <c>RequestAdditionalTime</c>. So for the entire stop this
/// service tells the SCM "I need 0 ms and my checkpoint is not advancing", which is the documented
/// signature of a hung service. Any caller following the SCM's wait algorithm — MSI included —
/// gives up almost immediately, long before the configured budget. The budget was never negotiated
/// with the thing that has to honour it.
/// </para>
///
/// <para>
/// Only <c>OnStop</c> is overridden. <c>OnShutdown</c> (machine shutdown) has the same shape, but
/// nothing is rewriting files then and Windows caps that budget itself, so the added complexity
/// would buy nothing.
/// </para>
/// </remarks>
internal sealed class ObsyncWindowsServiceLifetime : WindowsServiceLifetime
{
    /// <summary>
    /// How often to re-assert progress to the SCM while stopping. The SCM's documented wait
    /// algorithm continues only while the checkpoint keeps increasing, so this must be comfortably
    /// shorter than <see cref="ProgressWaitHint"/>.
    /// </summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>Time claimed at each heartbeat. Deliberately a small rolling promise rather than
    /// one large up-front claim: a caller then abandons us shortly after we actually stop making
    /// progress, instead of blocking for the whole shutdown budget on a service that is wedged.</summary>
    private static readonly TimeSpan ProgressWaitHint = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Grace after the host's own wait returns before declaring the drain failed.
    /// <c>ApplicationStopped</c> and the framework's internal stop signal fire from the same
    /// shutdown, and this avoids racing them by microseconds and killing a process that did in
    /// fact finish cleanly.
    /// </summary>
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(2);

    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<ObsyncWindowsServiceLifetime> _logger;
    private readonly TimeSpan _shutdownTimeout;

    public ObsyncWindowsServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _applicationLifetime = applicationLifetime;
        _logger = loggerFactory.CreateLogger<ObsyncWindowsServiceLifetime>();
        _shutdownTimeout = optionsAccessor.Value.ShutdownTimeout;
    }

    protected override void OnStop()
    {
        // Tell the SCM the truth for as long as we are still working. Each tick both refreshes the
        // wait hint and increments the checkpoint, which is what keeps a well-behaved caller
        // waiting; the framework's static checkPoint = 0 is why nothing waits today.
        var progress = new Timer(
            _ => TryRequestAdditionalTime(), state: null, dueTime: TimeSpan.Zero, period: ProgressInterval);
        try
        {
            base.OnStop();
        }
        finally
        {
            // Dispose-and-wait, not plain Dispose: ServiceBase reports SERVICE_STOPPED once this
            // method returns, and a heartbeat still in flight would be writing the service status
            // underneath that. This overload does not return until no callback is running.
            using var drained = new ManualResetEvent(initialState: false);
            progress.Dispose(drained);
            drained.WaitOne();
        }

        // base.OnStop() returning proves nothing: it returns both on a completed drain AND on an
        // expired ShutdownTimeout. ApplicationStopped is only signalled by a shutdown that actually
        // ran to completion, so it is the honest test.
        if (WaitedForCleanStop())
        {
            return;
        }

        // The host did not drain inside its budget. Leaving the process alive is the one option
        // that is definitely wrong: the SCM has already been told we stopped, so an upgrade is
        // about to write over files this process still holds. Terminate so the handles are released.
        //
        // The SCM sees a service process that died without reporting SERVICE_STOPPED and applies
        // the MSI's restart-on-failure recovery. That is the right outcome after a wedged drain
        // (the service comes back healthy) and is moot during an upgrade, where the service is
        // being deleted anyway.
        _logger.LogCritical(
            "The Obsync service did not finish stopping within {Timeout}s. Terminating so its files are "
            + "released — a stop that is reported but not achieved lets an upgrade overwrite a live process. "
            + "An in-flight run may be left to be recovered at next start.",
            _shutdownTimeout.TotalSeconds);

        Serilog.Log.CloseAndFlush();
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// True when the host completed a graceful shutdown, allowing <see cref="StoppedGrace"/> for
    /// the signal to land.
    /// </summary>
    private bool WaitedForCleanStop() =>
        _applicationLifetime.ApplicationStopped.IsCancellationRequested
        || _applicationLifetime.ApplicationStopped.WaitHandle.WaitOne(StoppedGrace);

    /// <summary>
    /// Claims more time from the SCM, tolerating the race where the service has already left the
    /// pending state — <c>RequestAdditionalTime</c> throws rather than no-ops in that case.
    /// </summary>
    private void TryRequestAdditionalTime()
    {
        try
        {
            RequestAdditionalTime(ProgressWaitHint);
        }
        catch (InvalidOperationException)
        {
            // Either no longer STOP_PENDING (the stop completed between the timer firing and this
            // call), or the lifetime was disposed underneath the timer — ObjectDisposedException
            // derives from InvalidOperationException, so this covers both.
        }
    }
}
