using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// The per-user data-root fallback, with its inputs supplied rather than read from the environment.
/// </summary>
/// <remarks>
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns
/// <see cref="string.Empty"/> — it does not throw — when the folder cannot be resolved, which is the
/// ordinary result for a service identity with no loaded profile. <c>Path.Combine("", "Obsync")</c>
/// then produced the RELATIVE path <c>"Obsync"</c>, and a service's working directory is
/// <c>C:\Windows\System32</c>: the service created and heartbeated into
/// <c>C:\Windows\System32\Obsync</c>, reported itself perfectly healthy, and the app never saw it.
/// <para>
/// The reason this needed injectable inputs is itself a finding: on a healthy machine the first step
/// always succeeds, so a test that reads the real environment can never reach the branch that
/// matters — which is exactly why the original test passed while both sides evaluated to "Obsync".
/// </para>
/// </remarks>
public sealed class ObsyncPathsDefaultRootTests
{
    [Fact]
    public void TheKnownFolder_IsUsedWhenItResolves()
    {
        var root = ObsyncPaths.DefaultRoot(@"C:\Users\alice\AppData\Local", null, null, null, out var warning);

        Assert.Equal(@"C:\Users\alice\AppData\Local\Obsync", root);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Obsync")]          // relative — the shape that produced System32\Obsync
    [InlineData(@"AppData\Local")]  // relative
    public void AnUnusableKnownFolder_NeverProducesARelativePath(string? knownFolder)
    {
        var root = ObsyncPaths.DefaultRoot(
            knownFolder, @"C:\Users\svc\AppData\Local", @"C:\Users\svc", @"C:\ProgramData", out var warning);

        Assert.True(Path.IsPathFullyQualified(root), $"the data root must be absolute; got '{root}'.");
        Assert.NotNull(warning);
    }

    [Fact]
    public void WithNoKnownFolder_TheEnvironmentVariableIsUsedNext()
    {
        var root = ObsyncPaths.DefaultRoot(
            string.Empty, @"C:\Users\svc\AppData\Local", @"C:\Users\svc", @"C:\ProgramData", out var warning);

        Assert.Equal(@"C:\Users\svc\AppData\Local\Obsync", root);
        Assert.Contains("profile", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNeither_TheUserProfileIsUsed()
    {
        var root = ObsyncPaths.DefaultRoot(string.Empty, null, @"C:\Users\svc", @"C:\ProgramData", out _);

        Assert.Equal(@"C:\Users\svc\AppData\Local\Obsync", root);
    }

    [Fact]
    public void WithNoPerAccountFolderAtAll_TheMachineWideFallbackIsUsed_AndSaysSo()
    {
        // Machine-wide is not per-user, so this is a real behavioural change and the warning has to
        // state it plainly — but a shared root that can be found beats a relative path under System32.
        var root = ObsyncPaths.DefaultRoot(string.Empty, null, null, @"C:\ProgramData", out var warning);

        Assert.Equal(@"C:\ProgramData\Obsync", root);
        Assert.Contains("share", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OBSYNC_DATA_ROOT", warning);
    }

    [Fact]
    public void EvenWithNothingUsable_TheResultIsStillAbsolute()
    {
        var root = ObsyncPaths.DefaultRoot(null, null, null, null, out var warning);

        Assert.True(Path.IsPathFullyQualified(root), $"the data root must be absolute; got '{root}'.");
        Assert.NotNull(warning);
    }

    [Fact]
    public void AFullyQualifiedOverride_StillWinsOverEverything()
    {
        Assert.Equal(@"C:\obsync-data", ObsyncPaths.ResolveRoot(@"C:\obsync-data"));
        Assert.Equal(@"C:\obsync-data", ObsyncPaths.ResolveRoot("  \"C:\\obsync-data\"  "));
    }

    [Fact]
    public void ARelativeOverride_IsRejectedAndTheDefaultIsUsed()
    {
        // Same reasoning as the fallback: a relative root resolves against the process working
        // directory, which differs between the app and the service — so the two hosts would silently
        // use different databases.
        var root = ObsyncPaths.ResolveRoot("obsync-data");

        Assert.True(Path.IsPathFullyQualified(root));
        Assert.NotEqual("obsync-data", root);
    }
}
