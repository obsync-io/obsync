namespace Obsync.Service;

/// <summary>
/// The time budget for stopping the service, and the external deadline it has to fit inside.
/// </summary>
/// <remarks>
/// These numbers are not independent, and getting that wrong is exactly what happened: the host was
/// given 90 seconds to drain and the process was terminated at 92, on the assumption that whoever
/// asked for the stop would wait that long. Windows Installer does not.
///
/// <para>
/// <b>The binding constraint</b> is the ServiceControl table's <c>Wait</c> column, documented as:
/// "Leaving this field null or entering a value of 1 causes the installer to wait <b>a maximum of
/// 30 seconds</b> for the service to complete before proceeding." The installer authoring uses
/// <c>Wait="yes"</c>, so an upgrade or an uninstall gives this service 30 seconds and then moves on
/// to DeleteServices and RemoveFiles REGARDLESS.
/// </para>
///
/// <para>
/// So a terminate that fires at 92 seconds is not a safety net; it is 60 seconds of nothing. By the
/// time it released the file handles, MSI had already tried to delete or overwrite the files those
/// handles were holding, and had already deferred them to a reboot. The whole point of terminating
/// is to release the handles <i>while the installer still cares</i>, which means the entire stop —
/// drain, bookkeeping, Quartz's wait, and the terminate — has to finish inside 30 seconds.
/// </para>
///
/// <para>
/// <see cref="Total"/> plus <see cref="TerminateGrace"/> is therefore the real promise, and
/// <c>ShutdownBudgetTests</c> pins it below <see cref="MsiStopServicesCap"/> with room to spare.
/// The remaining margin absorbs the SCM's own dispatch latency, which is not free and not measured.
/// </para>
///
/// <para>
/// Shrinking the budget costs less than it looks. The cooperative path is fast: interrupting a
/// Quartz job cancels the engine's token, the engine kills the git process tree and persists a
/// Cancelled run on a token that is deliberately NOT cancelled, and anything that still fails to
/// finish is recovered at next start by the orphaned-run cleaner. The old 90 seconds was headroom
/// for a case that never needed it, bought at the price of the case that did.
/// </para>
/// </remarks>
internal static class ServiceShutdownBudget
{
    /// <summary>
    /// How long Windows Installer waits for a service to stop before continuing anyway. Documented
    /// on the ServiceControl table's <c>Wait</c> column; not configurable from the package.
    /// </summary>
    public static readonly TimeSpan MsiStopServicesCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>HostOptions.ShutdownTimeout</c> — the whole budget, shared by every hosted service.
    /// </summary>
    public static readonly TimeSpan Total = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long <see cref="RunCancellationOnStopService"/> waits for interrupted runs to record
    /// themselves. Deliberately a fraction of <see cref="Total"/>, so the bookkeeping that stops
    /// after it and Quartz's own wait both still have a budget.
    /// </summary>
    public static readonly TimeSpan Drain = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace after the host's wait returns before the process is terminated, so a shutdown that
    /// completed in the same instant is not killed for a race.
    /// </summary>
    public static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(2);

    /// <summary>The longest this service can take to stop before its process is gone.</summary>
    public static TimeSpan WorstCaseStop => Total + TerminateGrace;
}
