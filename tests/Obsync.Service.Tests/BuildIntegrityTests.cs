using System.Reflection;
using Obsync.Service;

namespace Obsync.Service.Tests;

/// <summary>
/// The half-applied-upgrade guard: a new service host must refuse to run on old shared libraries.
/// </summary>
/// <remarks>
/// Reachable whenever someone answers "Do not close applications" to the installer's files-in-use
/// prompt — the service binary is replaced (its file was unlocked when the service was stopped and
/// deleted) while the shared Obsync DLLs the running desktop app holds are deferred to the next
/// reboot.
/// </remarks>
public sealed class BuildIntegrityTests
{
    private static string HostDirectory => AppContext.BaseDirectory;

    [Fact]
    public void AConsistentInstallation_ReportsNoMismatch()
    {
        // The test output directory is a real, coherent build of every Obsync assembly.
        Assert.Null(BuildIntegrity.DescribeMismatch(HostDirectory));
    }

    [Fact]
    public void AnOlderSharedLibrary_IsDetected()
    {
        // Same folder, but claiming to be a host from a different build — which is exactly the
        // shape of a deferred-file upgrade.
        var pretendNewerHost = new Version(999, 0, 0, 0);

        var mismatch = BuildIntegrity.DescribeMismatch(HostDirectory, pretendNewerHost);

        Assert.NotNull(mismatch);
        Assert.Contains("half upgraded", mismatch);
        Assert.Contains("restart the computer", mismatch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMessage_NamesTheOffendingAssembliesAndBothVersions()
    {
        var actual = typeof(BuildIntegrity).Assembly.GetName().Version!;
        var mismatch = BuildIntegrity.DescribeMismatch(HostDirectory, new Version(999, 0, 0, 0));

        // Support has to be able to tell WHICH files were left behind, and by how far.
        Assert.Contains("999.0.0.0", mismatch);
        Assert.Contains(actual.ToString(), mismatch);
        Assert.Contains("Obsync.", mismatch);
        Assert.EndsWith(".", mismatch);
    }

    [Fact]
    public void AFolderWithNoObsyncAssemblies_IsNotAMismatch()
    {
        var empty = Directory.CreateTempSubdirectory("obsync-integrity-");
        try
        {
            Assert.Null(BuildIntegrity.DescribeMismatch(empty.FullName, new Version(1, 0, 0, 0)));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMissingFolder_IsNotAMismatch()
    {
        // Never turn an unexpected layout into a refusal to start; the guard is for one specific,
        // provable condition.
        var missing = Path.Combine(Path.GetTempPath(), $"obsync-absent-{Guid.NewGuid():N}");
        Assert.Null(BuildIntegrity.DescribeMismatch(missing, new Version(1, 0, 0, 0)));
    }

    [Fact]
    public void ANonManagedFileMatchingThePattern_IsIgnored()
    {
        var directory = Directory.CreateTempSubdirectory("obsync-integrity-native-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Obsync.Native.dll"), "not an assembly");

            // A BadImageFormatException must be skipped, not surfaced as a version mismatch and not
            // thrown out of a method called on the service's startup path.
            Assert.Null(BuildIntegrity.DescribeMismatch(directory.FullName, new Version(1, 0, 0, 0)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheHostsOwnVersion_IsTheDefaultComparand()
    {
        // Guards the defaulting behavior the production call site relies on: Program.cs passes no
        // arguments at all.
        Assert.Equal(
            BuildIntegrity.DescribeMismatch(HostDirectory),
            BuildIntegrity.DescribeMismatch(HostDirectory, Assembly.GetAssembly(typeof(BuildIntegrity))!.GetName().Version));
    }
}
