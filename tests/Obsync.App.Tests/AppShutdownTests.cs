using Obsync.App.Services;

namespace Obsync.App.Tests;

/// <summary>
/// The close-confirmation must not appear when nobody can answer it.
/// </summary>
/// <remarks>
/// <c>MainWindow.OnClosing</c> asked "A sync is still running. Close anyway?" unconditionally, via
/// a blocking <c>ShowDialog</c>. WPF raises <c>Closing</c> even when the application is being shut
/// down by Windows, so an installer's Restart Manager — asking the app to close so it can replace
/// the files the app has open — got a modal dialog behind the installer's own progress window. The
/// installer then either force-terminated the app or gave up and deferred the file replacement to
/// the next reboot, which is how a new service host ends up running on old shared libraries.
/// </remarks>
public sealed class AppShutdownTests
{
    [Theory]
    // A person clicking the X with work in flight is the case the prompt exists for.
    [InlineData(true, false, true)]
    // Session ending: Restart Manager, logoff or restart. Not negotiable, so do not ask.
    [InlineData(true, true, false)]
    // Nothing to lose.
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void ConfirmsOnlyWhenThereIsSomethingToLoseAndSomeoneToAsk(
        bool hasActiveRuns, bool sessionEnding, bool expected)
    {
        Assert.Equal(expected, AppShutdown.ShouldConfirmClose(hasActiveRuns, sessionEnding));
    }

    [Fact]
    public void ASessionEnd_NeverPrompts_WhateverIsRunning()
    {
        // Stated separately from the table because it is the invariant an upgrade depends on:
        // there is no combination of state that lets a dialog block a session end.
        Assert.False(AppShutdown.ShouldConfirmClose(hasActiveRuns: true, sessionEnding: true));
        Assert.False(AppShutdown.ShouldConfirmClose(hasActiveRuns: false, sessionEnding: true));
    }
}
