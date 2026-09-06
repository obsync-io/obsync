using System.Xml.Linq;

namespace Obsync.Packaging.Tests;

/// <summary>
/// Structural guards over the installer's service-account logic — the part of the MSI that decides
/// which Windows account runs scheduled jobs, and therefore whether scheduling works at all.
///
/// These assert on the authored WiX source rather than a built MSI on purpose: they need no WiX
/// toolset, no publish tree and no MinGit download, so they run in the ordinary test pass. That
/// costs some fidelity — they cannot prove what the SCM does at install time — so they are written
/// to catch the specific class of defect this area has actually shipped: an authoring change that
/// silently rewires which property feeds the service logon account.
/// </summary>
public sealed class ServiceAccountWxsTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static readonly XDocument Installer =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Obsync.wxs"), LoadOptions.SetLineInfo);

    private static IEnumerable<XElement> Elements(string name) => Installer.Descendants(Wxs + name);

    private static XElement Property(string id) =>
        Elements("Property").Single(e => (string?)e.Attribute("Id") == id);

    /// <summary>The Publish rows on the Service Account dialog's Next button, keyed by Order.</summary>
    private static IEnumerable<XElement> NextPublishes() => Elements("Publish")
        .Where(e => (string?)e.Attribute("Dialog") == "ObsyncServiceAccountDlg"
            && (string?)e.Attribute("Control") == "Next");

    [Fact]
    public void RegistrySearch_NeverWritesStraightIntoSERVICE_ACCOUNT()
    {
        // The defect this guards: AppSearch overwrites a property that is already set, and runs at
        // sequence 50 of BOTH the UI and execute sequences. A RegistrySearch nested under
        // SERVICE_ACCOUNT therefore discarded every explicit answer — a command-line account on a
        // silent upgrade (applying the newly supplied password to the OLD account), and the
        // wizard's own answer, which the execute sequence's AppSearch clobbered after the fact.
        Assert.Empty(Property("SERVICE_ACCOUNT").Descendants(Wxs + "RegistrySearch"));
    }

    [Fact]
    public void SERVICE_ACCOUNT_IsOnlyEverDefaulted_NeverForced()
    {
        // Every SetProperty targeting SERVICE_ACCOUNT must yield to a value that is already set,
        // otherwise the remembered account stops being a default and becomes an override again.
        var setters = Elements("SetProperty")
            .Where(e => (string?)e.Attribute("Id") == "SERVICE_ACCOUNT")
            .ToList();

        Assert.NotEmpty(setters);
        foreach (var setter in setters)
        {
            Assert.Contains("NOT SERVICE_ACCOUNT", (string?)setter.Attribute("Condition") ?? string.Empty);
        }
    }

    [Fact]
    public void TheLogonAccountDefault_PrefersTheLiveServiceConfiguration()
    {
        // Reading the SCM's own ObjectName is what makes an out-of-band `sc.exe config` or Log On
        // tab change survive a repair or upgrade instead of being reverted to our stale copy.
        var searches = Elements("RegistrySearch").ToList();

        Assert.Contains(searches, s =>
            (string?)s.Attribute("Key") == @"SYSTEM\CurrentControlSet\Services\Obsync"
            && (string?)s.Attribute("Name") == "ObjectName");
        Assert.Contains(searches, s =>
            (string?)s.Attribute("Key") == @"SOFTWARE\Obsync"
            && (string?)s.Attribute("Name") == "ServiceAccount");

        // With no service and no remembered value — a first install — the default is LocalSystem,
        // which survives because a failed AppSearch leaves the Property-table value untouched.
        Assert.Equal("LocalSystem", (string?)Property("SERVICE_ACCOUNT_REMEMBERED").Attribute("Value"));
    }

    [Fact]
    public void ABlankPassword_IsRefusedExceptForAccountsWindowsLogsOnWithoutOne()
    {
        // An empty Formatted value reaches CreateService as NULL. That is correct for a gMSA or a
        // built-in/virtual service account and wrong for everything else, where the service installs
        // and then cannot log on. The click-through upgrade hits this every time: the account is
        // prefilled from the registry and the password box never is.
        var guard = NextPublishes().Single(e => (string?)e.Attribute("Value") == "ObsyncPasswordRequiredDlg");
        var condition = (string)guard.Attribute("Condition")!;

        Assert.Contains("NOT SERVICE_ACCOUNT_PWD", condition);
        foreach (var passwordless in PasswordlessAccountTests)
        {
            Assert.Contains(passwordless, condition);
        }

        Assert.Contains(Elements("Dialog"), d => (string?)d.Attribute("Id") == "ObsyncPasswordRequiredDlg");
    }

    [Fact]
    public void TheAdvanceCondition_ExactlyComplementsTheTwoBlockingGuards()
    {
        // If the blocking guard and the advance condition disagree, the wizard shows the error
        // dialog and then advances anyway — the modal is dismissed and the bad value is committed.
        var advance = (string)NextPublishes()
            .Single(e => (string?)e.Attribute("Event") == "NewDialog")
            .Attribute("Condition")!;

        // An account name is required...
        Assert.Contains("SERVICE_ACCOUNT_NAME", advance);
        // ...and so is a password, unless the account is one of the passwordless kinds.
        Assert.Contains("SERVICE_ACCOUNT_PWD", advance);
        foreach (var passwordless in PasswordlessAccountTests)
        {
            Assert.Contains(passwordless, advance);
        }
    }

    [Fact]
    public void TheGuards_RunBeforeTheAdvance()
    {
        // MSI evaluates Publish rows in Order. A guard sequenced after the NewDialog row would
        // never be reached.
        // Event rows only — the property-setting rows share Value="{}" and carry no dialog name.
        var orderOf = NextPublishes()
            .Where(e => e.Attribute("Event") is not null)
            .ToDictionary(
                e => (string?)e.Attribute("Value") ?? string.Empty,
                e => int.Parse((string)e.Attribute("Order")!));

        var advance = orderOf["VerifyReadyDlg"];
        Assert.True(orderOf["ObsyncAccountRequiredDlg"] < advance);
        Assert.True(orderOf["ObsyncPasswordRequiredDlg"] < advance);
    }

    [Fact]
    public void ThePasswordNeverLeaksIntoAnInstallerLog_AndBothPropertiesCrossToTheExecuteSequence()
    {
        // Secure lets an elevated/silent install set them and carries the dialog's answers across
        // the UI -> execute boundary; Hidden keeps the password out of a /l*v log.
        Assert.Equal("yes", (string?)Property("SERVICE_ACCOUNT").Attribute("Secure"));
        Assert.Equal("yes", (string?)Property("SERVICE_PASSWORD").Attribute("Secure"));
        Assert.Equal("yes", (string?)Property("SERVICE_PASSWORD").Attribute("Hidden"));

        // The dialog's staging copy is UI-only, so it must be Hidden but must NOT be Secure.
        Assert.Equal("yes", (string?)Property("SERVICE_ACCOUNT_PWD").Attribute("Hidden"));
        Assert.Null(Property("SERVICE_ACCOUNT_PWD").Attribute("Secure"));
    }

    [Fact]
    public void TheServiceLogonBindsToTheProperties_UnderTheNameTheAppLooksFor()
    {
        // "Obsync" is also the name SchedulerHealthService queries and AddWindowsService registers;
        // a rename on one side alone silently breaks the app's scheduler-health check.
        var install = Elements("ServiceInstall").Single();

        Assert.Equal("Obsync", (string?)install.Attribute("Name"));
        Assert.Equal("[SERVICE_ACCOUNT]", (string?)install.Attribute("Account"));
        Assert.Equal("[SERVICE_PASSWORD]", (string?)install.Attribute("Password"));
    }

    /// <summary>
    /// The account shapes Windows logs on without a password: a gMSA (trailing "$"), a virtual
    /// account, and the built-in service accounts. Written as the MSI condition fragments that must
    /// appear on both sides of the guard, so the two conditions cannot drift apart.
    /// </summary>
    private static readonly string[] PasswordlessAccountTests =
    [
        "SERVICE_ACCOUNT_NAME >> \"$\"",
        "SERVICE_ACCOUNT_NAME ~<< \"NT AUTHORITY\\\"",
        "SERVICE_ACCOUNT_NAME ~<< \"NT SERVICE\\\"",
    ];

    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    [Fact]
    public void TheInstaller_GrantsLogOnAsAService()
    {
        // INSTALL.md claimed this already happened "by the installer's service assignment". Nothing
        // did it: MSI's CreateService does not grant SeServiceLogonRight, and there was no util:User,
        // no LsaAddAccountRights and no NTRIGHTS anywhere. So a domain account chosen in the wizard
        // installed cleanly and then failed to start with Error 1069 — which reads as a wrong
        // password and is not one. The installer is the last elevated moment where this can happen
        // without a second admin step.
        var user = Assert.Single(Installer.Descendants(Util + "User"));

        Assert.Equal("[SERVICE_ACCOUNT]", (string?)user.Attribute("Name"));
        Assert.Equal("yes", (string?)user.Attribute("LogonAsService"));

        // Must only ADD the right. The account already exists — domain, gMSA, or built-in — and
        // creating or modifying the principal is emphatically not the installer's business.
        Assert.Equal("no", (string?)user.Attribute("CreateUser"));
        Assert.Equal("yes", (string?)user.Attribute("UpdateIfExists"));
    }

    [Fact]
    public void TheLogonRightGrant_IsSkippedForTheBuiltInServicePrincipals()
    {
        // LocalSystem, LocalService and NetworkService hold the right implicitly. Naming one would
        // send the custom action looking up a principal the grant means nothing for — and
        // LocalSystem is the DEFAULT, so this fires on the most common install of all.
        var component = Installer.Descendants(Wxs + "Component")
            .Single(e => (string?)e.Attribute("Id") == "ServiceLogonRight");
        var condition = (string?)component.Attribute("Condition") ?? string.Empty;

        Assert.Contains("SERVICE_ACCOUNT", condition);
        Assert.Contains("LocalSystem", condition);
        Assert.Contains("LocalService", condition);
        Assert.Contains("NetworkService", condition);
        Assert.Contains(@"NT AUTHORITY\", condition);
        Assert.Contains(@"NT SERVICE\", condition);

        // The grant lives in this component, so the condition actually gates it.
        Assert.Single(component.Descendants(Util + "User"));

        // ...and the component is installed, not authored and forgotten.
        Assert.Contains(Installer.Descendants(Wxs + "ComponentRef"),
            e => (string?)e.Attribute("Id") == "ServiceLogonRight");
    }

    [Fact]
    public void AGmsa_StillGetsTheLogonRight()
    {
        // A gMSA is passwordless but is NOT a built-in principal: it needs SeServiceLogonRight like
        // any other account. Excluding it (e.g. by reusing the dialog's ends-with-"$" allow-list)
        // would reproduce the 1069 this fix exists to remove.
        var component = Installer.Descendants(Wxs + "Component")
            .Single(e => (string?)e.Attribute("Id") == "ServiceLogonRight");

        Assert.DoesNotContain("$", (string?)component.Attribute("Condition") ?? string.Empty);
    }

    [Fact]
    public void ASilentInstall_RefusesAnAccountWithNoPassword()
    {
        // The interactive guard is a dialog Publish, so it exists only in the UI sequence:
        // `msiexec /qn SERVICE_ACCOUNT="DOMAIN\user"` with no password sailed past it and installed a
        // service that can never log on, reporting success. Silent installs are how this reaches
        // fleets, so the failure arrived at scale with nothing to read.
        var launch = Assert.Single(Elements("Launch"));
        var condition = (string?)launch.Attribute("Condition") ?? string.Empty;

        // Only where the dialog cannot run (2 = none, 3 = basic).
        Assert.Contains("UILevel", condition);
        Assert.Contains("SERVICE_PASSWORD", condition);

        // The same passwordless allow-list the dialog uses, or the gate would block legitimate
        // gMSA and built-in-account installs.
        Assert.Contains(@"""$""", condition);
        Assert.Contains(@"NT AUTHORITY\", condition);
        Assert.Contains("LocalSystem", condition);
        Assert.Contains("1069", (string?)launch.Attribute("Message") ?? string.Empty);
    }
}
