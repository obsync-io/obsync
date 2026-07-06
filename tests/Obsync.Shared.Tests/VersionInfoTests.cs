using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

public sealed class VersionInfoTests
{
    [Fact]
    public void Of_ReturnsTheVersionPrefix_WithoutBuildMetadata()
    {
        var version = VersionInfo.Of(typeof(VersionInfo).Assembly);

        // Deliberately not pinned to the release number (that broke on every version bump):
        // the contract is "a clean semver prefix, no +<gitsha> build metadata".
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version);
    }
}
