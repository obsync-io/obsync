using System.IO;
using Obsync.App.Services;

namespace Obsync.App.Tests;

/// <summary>Storage-health arithmetic: recursive directory sizes, file sizes, and formatting.</summary>
public sealed class StorageUsageTests
{
    [Fact]
    public void DirectorySizeBytes_SumsFilesRecursively()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"obsync-size-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[100]);
            var nested = Directory.CreateDirectory(Path.Combine(root, "sub", "deeper")).FullName;
            File.WriteAllBytes(Path.Combine(nested, "b.bin"), new byte[250]);

            Assert.Equal(350, StorageUsage.DirectorySizeBytes(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectorySizeBytes_MissingDirectory_IsZero()
    {
        Assert.Equal(0, StorageUsage.DirectorySizeBytes(
            Path.Combine(Path.GetTempPath(), $"obsync-none-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void FileSizeBytes_ReturnsTheLength_OrNullWhenMissing()
    {
        var file = Path.Combine(Path.GetTempPath(), $"obsync-size-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(file, new byte[42]);
        try
        {
            Assert.Equal(42L, StorageUsage.FileSizeBytes(file));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Null(StorageUsage.FileSizeBytes(file));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(812, "812 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void FormatBytes_IsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, StorageUsage.FormatBytes(bytes));
    }
}
