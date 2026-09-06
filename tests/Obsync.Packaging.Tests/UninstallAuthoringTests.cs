using System.Xml.Linq;

namespace Obsync.Packaging.Tests;

/// <summary>
/// The authoring that decides whether uninstalling actually removes the product.
/// </summary>
/// <remarks>
/// An audit walked every artifact the package creates against a removal path and found the MSI half
/// correct — service, event source, registry values, PATH entry, shortcut and ARP entry all go. None
/// of it was asserted anywhere, so all of it was one careless edit from regressing. These pin the
/// properties that make removal work, at the level this project can see: the authored XML. The
/// compiled consequences (Wix4User attributes 864, ServiceControl Event 162) were confirmed by
/// building the package and reading its tables, which no test here does.
/// </remarks>
public sealed class UninstallAuthoringTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static readonly XDocument Installer =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Obsync.wxs"));

    private static IEnumerable<XElement> Elements(string name) => Installer.Descendants(Wxs + name);

    [Fact]
    public void NoComponent_IsMarkedPermanent()
    {
        // A permanent component is never removed, and nothing here should be. It is also the classic
        // way an installer quietly stops being uninstallable, because the attribute reads as
        // harmless.
        var permanent = Elements("Component")
            .Where(c => (string?)c.Attribute("Permanent") == "yes")
            .Select(c => (string?)c.Attribute("Id"))
            .ToList();

        Assert.True(permanent.Count == 0, $"Permanent components: {string.Join(", ", permanent)}");
    }

    [Fact]
    public void ThePathEntry_IsRemovedOnUninstall()
    {
        // Permanent="no" is what compiles the leading '-' into the Environment row's Name, and the
        // '-' is the entire mechanism by which RemoveEnvironmentStrings takes the entry back out.
        // Without it the install directory stays on the machine PATH forever, pointing at a folder
        // that no longer exists.
        var path = Assert.Single(Elements("Environment"), e => (string?)e.Attribute("Name") == "PATH");

        Assert.Equal("no", (string?)path.Attribute("Permanent"));
        Assert.Equal("set", (string?)path.Attribute("Action"));
        Assert.Equal("yes", (string?)path.Attribute("System"));
    }

    [Fact]
    public void EveryComponent_IsReachableFromTheFeature()
    {
        // A component no feature references is never installed — and, more to the point here, an
        // orphan would never be scheduled for removal either.
        var referenced = Elements("ComponentRef")
            .Select(r => (string?)r.Attribute("Id"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        var unreferenced = Elements("Component")
            .Select(c => (string?)c.Attribute("Id"))
            .Where(id => id is not null && !referenced.Contains(id))
            .ToList();

        Assert.True(
            unreferenced.Count == 0,
            $"Components not referenced by any feature: {string.Join(", ", unreferenced)}");
    }

    [Fact]
    public void EveryBookkeepingRegistryValue_LivesInAComponentThatIsRemoved()
    {
        // The four HKLM values under SOFTWARE\Obsync are what let an upgrade remember the service
        // account and the install folder. They must also disappear on uninstall, or a later fresh
        // install silently inherits a previous installation's answers.
        foreach (var value in Elements("RegistryValue"))
        {
            var key = (string?)value.Attribute("Key") ?? string.Empty;
            if (!key.Contains("Obsync", StringComparison.OrdinalIgnoreCase)
                || key.Contains("EventLog", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var component = value.Ancestors(Wxs + "Component").FirstOrDefault();
            Assert.True(component is not null, $"RegistryValue {(string?)value.Attribute("Name")} has no component.");
            Assert.NotEqual("yes", (string?)component!.Attribute("Permanent"));
        }
    }

    [Fact]
    public void NothingRunsAsACustomActionDuringUninstall()
    {
        // The only custom action is the finish-page launch, and it is reachable solely from a dialog
        // Publish conditioned on NOT Installed. An uninstall must never start the application it is
        // in the middle of deleting.
        var action = Assert.Single(Elements("CustomAction"));
        Assert.Equal("LaunchObsyncApp", (string?)action.Attribute("Id"));

        var publishes = Elements("Publish")
            .Where(p => (string?)p.Attribute("Value") == "LaunchObsyncApp")
            .ToList();

        Assert.NotEmpty(publishes);
        Assert.All(publishes, p =>
            Assert.Contains("NOT Installed", (string?)p.Attribute("Condition") ?? string.Empty));
    }

    [Fact]
    public void TheServiceIsStoppedAndRemoved_AndTheStopIsWaitedFor()
    {
        // Files cannot be deleted under a running service, so the stop has to be synchronous. The
        // start deliberately is not — that asymmetry is why there are two ServiceControl elements.
        var remove = Assert.Single(
            Elements("ServiceControl"), c => (string?)c.Attribute("Remove") == "uninstall");

        Assert.Equal("Obsync", (string?)remove.Attribute("Name"));
        Assert.Equal("both", (string?)remove.Attribute("Stop"));
        Assert.Equal("yes", (string?)remove.Attribute("Wait"));
    }

    [Fact]
    public void TheInstallerNeverReachesOutsideTheInstallFolder()
    {
        // What makes retained user data safe is not a policy, it is that no element in the package
        // names a path outside INSTALLFOLDER. Uninstall therefore cannot touch the database, the git
        // workspaces or the logs. Pinned so that stays true by construction rather than by luck.
        Assert.Empty(Elements("RemoveFile"));
        Assert.Empty(Elements("RemoveFolder"));

        var directories = Elements("Directory")
            .Select(d => (string?)d.Attribute("Id"))
            .Where(id => id is not null)
            .ToList();

        Assert.All(directories, id =>
            Assert.True(
                id is "INSTALLFOLDER" or "ProgramMenuFolder" or "ProgramFiles64Folder",
                $"Unexpected directory '{id}' — the package should only address the install folder "
                + "and the Start Menu."));
    }
}
