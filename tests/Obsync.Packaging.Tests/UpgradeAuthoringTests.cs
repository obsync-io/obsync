using System.Xml.Linq;

namespace Obsync.Packaging.Tests;

/// <summary>
/// The authoring that decides whether an upgrade lands where the previous install was, replaces it
/// rather than joining it, and leaves the service account's rights intact.
/// </summary>
/// <remarks>
/// None of this was asserted anywhere, which is why all of it was wrong at once. These read the
/// authored WiX source; the compiled consequences (Upgrade row attributes 513, Wix4User attributes
/// 864, SetINSTALLFOLDER at sequence 51, SetARPINSTALLLOCATION at 1001) were confirmed separately by
/// building the package and reading its tables back through the Windows Installer COM API.
/// </remarks>
public sealed class UpgradeAuthoringTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    private static readonly XDocument Installer =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Obsync.wxs"));

    private static IEnumerable<XElement> Elements(string name) => Installer.Descendants(Wxs + name);

    private static XElement SetPropertyFor(string id) =>
        Assert.Single(Elements("SetProperty"), e => (string?)e.Attribute("Id") == id);

    [Fact]
    public void TheUpgradeCode_IsUnchanged()
    {
        // A changed UpgradeCode makes every already-shipped version un-upgradeable, permanently and
        // silently — the new package simply installs alongside the old one. It is the one value in
        // this file that can never be corrected after the fact, so it is pinned literally.
        Assert.Equal(
            "7B2E9E9C-3C1E-4C7A-9E2B-0A1F6D5C4B30",
            (string?)Assert.Single(Elements("Package")).Attribute("UpgradeCode"));
    }

    [Fact]
    public void RebuildingTheSameVersion_IsTreatedAsAnUpgrade()
    {
        // No ProductCode is authored, so WiX generates a fresh one per build and every BUILD of a
        // version is a distinct product. Without this attribute WiX emits VersionMax exclusive, so a
        // rebuilt package matched neither the upgrade nor the downgrade row and installed side by
        // side: two identical ARP entries over one set of refcounted components, where the first
        // uninstall removes nothing at all.
        Assert.Equal("yes", (string?)Assert.Single(Elements("MajorUpgrade")).Attribute("AllowSameVersionUpgrades"));
    }

    [Fact]
    public void TheInstallFolder_IsRememberedAcrossUpgrades()
    {
        // An upgrade is a fresh install under a new ProductCode, so without this the directory fell
        // back to the Directory-table default: a non-default install was silently relocated to
        // Program Files, and a silent upgrade did it with no prompt and exit 0.
        var search = Assert.Single(
            Elements("RegistrySearch"), e => (string?)e.Attribute("Id") == "RememberedInstallFolder");

        Assert.Equal("HKLM", (string?)search.Attribute("Root"));
        Assert.Equal(@"SOFTWARE\Obsync", (string?)search.Attribute("Key"));
        Assert.Equal("InstallFolder", (string?)search.Attribute("Name"));

        // Into its OWN property, never straight into INSTALLFOLDER: AppSearch overwrites whatever is
        // already there and runs in both sequences, which is the trap the service-account plumbing
        // documents at length.
        Assert.Equal("INSTALLFOLDER_REMEMBERED", (string?)search.Parent!.Attribute("Id"));
    }

    [Fact]
    public void TheRememberedFolder_IsOnlyADefault()
    {
        // An explicit INSTALLFOLDER= on the command line has to keep winning, or a deliberate
        // relocation becomes impossible.
        var set = SetPropertyFor("INSTALLFOLDER");

        Assert.Equal("[INSTALLFOLDER_REMEMBERED]", (string?)set.Attribute("Value"));
        Assert.Contains("NOT INSTALLFOLDER", (string?)set.Attribute("Condition"));
        Assert.Equal("AppSearch", (string?)set.Attribute("After"));
    }

    [Fact]
    public void TheInstallFolder_IsWrittenBackForTheNextUpgradeToFind()
    {
        // The search above finds nothing unless something records it.
        var value = Assert.Single(
            Elements("RegistryValue"), e => (string?)e.Attribute("Name") == "InstallFolder");

        Assert.Equal("HKLM", (string?)value.Attribute("Root"));
        Assert.Equal(@"SOFTWARE\Obsync", (string?)value.Attribute("Key"));
        Assert.Equal("[INSTALLFOLDER]", (string?)value.Attribute("Value"));
    }

    [Fact]
    public void TheInstallLocation_IsPublishedToAddRemovePrograms()
    {
        // It was empty, so an admin could not read the current path back out of ARP to know what to
        // re-pass on a silent upgrade. Must run after CostFinalize, which is where INSTALLFOLDER
        // acquires its resolved value.
        var set = SetPropertyFor("ARPINSTALLLOCATION");

        Assert.Equal("[INSTALLFOLDER]", (string?)set.Attribute("Value"));
        Assert.Equal("CostFinalize", (string?)set.Attribute("After"));
    }

    [Fact]
    public void UninstallingDoesNotRevokeLogOnAsAService()
    {
        // WiX's default for this is YES, and the comment in the authoring used to claim the opposite
        // was already true. Two consequences: uninstall revoked a right the site may have granted
        // for its own reasons, and — because Wix4RemoveUser is a COMMIT action while the new
        // product's grant is undone by its rollback CA — a FAILED upgrade left the service account
        // without the right, producing an Error 1069 that reads as a bad password.
        var user = Assert.Single(Installer.Descendants(Util + "User"));

        Assert.Equal("no", (string?)user.Attribute("RemoveOnUninstall"));
        Assert.Equal("no", (string?)user.Attribute("CreateUser"));
        Assert.Equal("yes", (string?)user.Attribute("LogonAsService"));
    }
}
