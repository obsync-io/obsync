using Obsync.App.Services;

namespace Obsync.App.Tests;

/// <summary>
/// Pins the version arithmetic behind the update notification: tag parsing tolerates the common
/// GitHub shapes (v-prefix, 2–4 components), and anything unparseable is never treated as newer —
/// a bad tag must silently mean "no update", not a false alarm.
/// </summary>
public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.2", 1, 2, -1, -1)]
    [InlineData("1.2.3", 1, 2, 3, -1)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("v0.4.0", 0, 4, 0, -1)]
    [InlineData("V2.0", 2, 0, -1, -1)]
    [InlineData(" v1.0.1 ", 1, 0, 1, -1)]
    public void TryParseVersion_AcceptsReleaseTagShapes(string tag, int major, int minor, int build, int revision)
    {
        var version = UpdateChecker.TryParseVersion(tag);

        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor), new Version(version!.Major, version.Minor));
        Assert.Equal(build, version.Build);
        Assert.Equal(revision, version.Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]                 // a single component is not a version
    [InlineData("v1")]
    [InlineData("latest")]
    [InlineData("release-2026-07")]
    [InlineData("1.2.3-beta")]        // prerelease tags are deliberately not announced
    [InlineData("v1.2.3-rc.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("vv1.2.3")]
    [InlineData("1..2")]
    public void TryParseVersion_RejectsGarbageAndPrereleases(string? tag) =>
        Assert.Null(UpdateChecker.TryParseVersion(tag));

    [Theory]
    [InlineData("v0.5.0", "0.4.0")]
    [InlineData("0.4.1", "0.4.0")]
    [InlineData("v1.0", "0.9.9")]
    [InlineData("v0.4.0.1", "0.4.0")]   // a 4th component counts
    [InlineData("V2.0.0", "1.9.9.9")]
    public void IsNewer_TrueForStrictlyNewerReleases(string latestTag, string current) =>
        Assert.True(UpdateChecker.IsNewer(latestTag, current));

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]     // equal
    [InlineData("v0.4", "0.4.0")]       // equal once missing components count as zero
    [InlineData("0.4.0", "0.4")]
    [InlineData("v0.3.9", "0.4.0")]     // older
    [InlineData("v0.4.0", "0.4.1")]
    [InlineData("latest", "0.4.0")]     // unparseable tag → never an update
    [InlineData("v1.2.3-beta", "0.4.0")]
    [InlineData("", "0.4.0")]
    [InlineData(null, "0.4.0")]
    [InlineData("v9.9.9", "garbage")]   // unparseable current → stay silent
    [InlineData("v9.9.9", null)]
    public void IsNewer_FalseForEqualOlderOrUnparseable(string? latestTag, string? current) =>
        Assert.False(UpdateChecker.IsNewer(latestTag, current));
}
