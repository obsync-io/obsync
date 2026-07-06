using Obsync.Shared;
using Xunit;

namespace Obsync.Shared.Tests;

public sealed class VersionInfoTests
{
    [Fact]
    public void Of_ReturnsTheVersionPrefix_WithoutBuildMetadata()
    {
        var version = VersionInfo.Of(typeof(VersionInfo).Assembly);

        Assert.StartsWith("0.2.0", version);      // from Directory.Build.props <VersionPrefix>
        Assert.DoesNotContain("+", version);       // the +<gitsha> suffix is stripped
    }
}
