namespace Obsync.Git.Tests;

/// <summary>Serializes tests that mutate process environment variables against everything else.</summary>
[CollectionDefinition(EnvironmentCollection.Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment";
}

/// <summary>
/// Pins the git executable resolution order used by <see cref="GitCommandRunner"/>:
/// OBSYNC_GIT environment variable → bundled tools\git\cmd\git.exe → "git" from PATH.
/// The tests drive <see cref="GitCommandRunner.ResolveGitExecutable"/> directly because the
/// public <see cref="GitCommandRunner.GitExecutable"/> caches its answer for the process lifetime.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class GitExecutableResolutionTests : IDisposable
{
    private const string EnvVar = "OBSYNC_GIT";

    private readonly string? _originalEnvValue;
    private readonly string _root;

    public GitExecutableResolutionTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVar);
        _root = Path.Combine(Path.GetTempPath(), "obsync-git-resolve-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _originalEnvValue);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnvironmentVariable_PointingAtExistingFile_WinsOverBundled()
    {
        var overrideGit = CreateFakeGit(Path.Combine(_root, "override", "git.exe"));
        var baseDir = Path.Combine(_root, "app");
        CreateFakeGit(Path.Combine(baseDir, "tools", "git", "cmd", "git.exe"));
        Environment.SetEnvironmentVariable(EnvVar, overrideGit);

        Assert.Equal(overrideGit, GitCommandRunner.ResolveGitExecutable(baseDir));
    }

    [Fact]
    public void EnvironmentVariable_PointingAtMissingFile_IsIgnored()
    {
        Environment.SetEnvironmentVariable(EnvVar, Path.Combine(_root, "does-not-exist", "git.exe"));

        Assert.Equal("git", GitCommandRunner.ResolveGitExecutable(Path.Combine(_root, "app")));
    }

    [Fact]
    public void BundledGit_WhenPresent_IsUsed()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var baseDir = Path.Combine(_root, "app");
        var bundled = CreateFakeGit(Path.Combine(baseDir, "tools", "git", "cmd", "git.exe"));

        Assert.Equal(bundled, GitCommandRunner.ResolveGitExecutable(baseDir));
    }

    [Fact]
    public void NoOverrideAndNoBundledGit_FallsBackToPath()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        Assert.Equal("git", GitCommandRunner.ResolveGitExecutable(Path.Combine(_root, "app")));
    }

    private static string CreateFakeGit(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake git");
        return path;
    }
}
