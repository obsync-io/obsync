namespace Obsync.App.Services;

/// <summary>
/// Whether closing the main window should stop to ask the user first.
/// </summary>
/// <remarks>
/// The confirmation exists so a person does not throw away a running sync with a stray click. But
/// it was unconditional, and a modal is exactly the wrong thing when nobody is at the keyboard.
///
/// <para>
/// The case that matters is an upgrade. The MSI's Restart Manager asks this app to close so it can
/// replace the files the app has open; for a windowed application that request arrives as
/// WM_QUERYENDSESSION, which WPF raises as <c>Application.SessionEnding</c> and then follows by
/// closing every window — and WPF still raises <c>Closing</c> on that path. So a sync running at
/// upgrade time popped a "Close anyway?" dialog behind the installer's progress window, waiting on
/// a human who was watching the installer. Restart Manager eventually force-terminates or gives up
/// and schedules the file replacement for the next reboot, which is the route to a service running
/// on half-replaced binaries. The same thing happened on an ordinary Windows logoff or restart.
/// </para>
///
/// <para>
/// Kept as a pure function rather than a flag read inside the window so the rule can be tested
/// without standing up WPF.
/// </para>
/// </remarks>
public static class AppShutdown
{
    /// <summary>
    /// True only when there is something worth interrupting AND a human is there to answer.
    /// </summary>
    /// <param name="hasActiveRuns">Whether a sync is currently executing in this process.</param>
    /// <param name="sessionEnding">
    /// Whether Windows (or Restart Manager, on behalf of an installer) is ending the session. When
    /// it is, the close is not negotiable and prompting only delays it.
    /// </param>
    public static bool ShouldConfirmClose(bool hasActiveRuns, bool sessionEnding) =>
        hasActiveRuns && !sessionEnding;
}
