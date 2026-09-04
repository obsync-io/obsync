using Obsync.Shared;

namespace Obsync.Shared.Tests;

/// <summary>
/// Covers the OBSYNC_DATA_ROOT override. The value decides which database every host binds to, so a
/// value one host accepts and another resolves differently splits the app and the service onto two
/// databases — which surfaces to the user as the service running under the wrong account, a cause
/// they cannot fix by changing the account.
/// </summary>
public sealed class ObsyncPathsTests
{
    private static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obsync");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void NoOverride_UsesThePerUserDefault(string? raw)
    {
        Assert.Equal(DefaultRoot, ObsyncPaths.ResolveRoot(raw));
    }

    [Theory]
    [InlineData(@"relative\path")]
    [InlineData(@".\data")]
    [InlineData(@"..\data")]
    [InlineData(@"\rooted-but-not-qualified")] // rooted, yet still drive-relative
    [InlineData("C:")]                          // drive-relative: resolves against the CWD on C:
    public void ARelativeOverride_IsRejected_BecauseItResolvesPerHost(string raw)
    {
        // The app's working directory is its install folder; a Windows service's is
        // C:\Windows\System32. Honouring a relative value would give them different roots.
        Assert.Equal(DefaultRoot, ObsyncPaths.ResolveRoot(raw));
    }

    [Theory]
    [InlineData(@"D:\ObsyncData")]
    [InlineData(@"  D:\ObsyncData  ")]
    [InlineData(@"""D:\ObsyncData""")]
    [InlineData(@" ""D:\ObsyncData"" ")]
    public void AFullyQualifiedOverride_IsHonoured_AfterTrimmingQuotesAndSpace(string raw)
    {
        // Quotes and whitespace survive being pasted into the Windows environment editor.
        Assert.Equal(@"D:\ObsyncData", ObsyncPaths.ResolveRoot(raw));
    }

    [Fact]
    public void AnOverrideIsNormalised_SoTwoSpellingsOfOnePathAgree()
    {
        Assert.Equal(@"D:\ObsyncData", ObsyncPaths.ResolveRoot(@"D:\x\..\ObsyncData"));

        // A trailing separator is deliberately left alone: Path.Combine handles it, and stripping
        // it would turn a drive root ("D:\") into the drive-relative "D:".
        Assert.Equal(@"D:\ObsyncData\obsync.db", Path.Combine(ObsyncPaths.ResolveRoot(@"D:\ObsyncData\"), "obsync.db"));
        Assert.Equal(@"D:\", ObsyncPaths.ResolveRoot(@"D:\"));
    }

    [Fact]
    public void AnUnusableOverride_FallsBackInsteadOfThrowing()
    {
        // This runs in a static initializer, so throwing would kill the process before any logger
        // exists — which is exactly the failure that made a bad root undiagnosable in the service.
        Assert.Equal(DefaultRoot, ObsyncPaths.ResolveRoot("D:\\bad\0path"));
    }
}
