namespace Obsync.App.Services;

/// <summary>
/// What the user needs to know before running an Obsync installer, in one place so the startup
/// toast and the About tab cannot drift apart.
/// </summary>
/// <remarks>
/// The update notification used to be a bare "a new version is available" plus a link to the
/// release page. Meanwhile the product knew — and documented, in its own installer source and in
/// INSTALL.md — four things that determine whether that upgrade succeeds, and told the user none
/// of them. Every one of these is a failure someone would otherwise hit and have to diagnose:
///
/// <list type="bullet">
/// <item>The upgrade must replace files this very application has open.</item>
/// <item>An open service list (services.msc, Server Manager) holds a handle to the Obsync service,
/// which the upgrade deletes and re-creates; the handle keeps the delete pending and the re-create
/// fails with ERROR_SERVICE_MARKED_FOR_DELETE. Windows Installer reports that as error 1923,
/// "verify that you have sufficient privileges", which sends people to audit permissions for what
/// is not a permissions problem.</item>
/// <item>An unattended upgrade of a service running as a named account must re-supply its password:
/// Windows will not hand it back, and the upgrade re-creates the service rather than reconfiguring
/// it.</item>
/// <item>The MSI is not code-signed, so Windows warns about an unrecognized publisher.</item>
/// </list>
/// </remarks>
public static class UpgradeGuidance
{
    /// <summary>One line, for the startup toast, where there is room for the single most likely trap.</summary>
    public const string ShortNotice = "Close Obsync and any open services list before installing.";

    /// <summary>The full set, for the About tab, which has room to explain.</summary>
    public const string BeforeYouInstall =
        "Before installing: close this window, and close services.msc or Server Manager if open — "
        + "the upgrade replaces files Obsync holds and re-creates its Windows service, and an open "
        + "service list makes that fail. If the service runs as a named account, an unattended "
        + "upgrade needs its password supplied again. The installer is not yet code-signed, so "
        + "Windows will warn about an unrecognized publisher.";
}
