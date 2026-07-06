using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.App.Services;
using Obsync.Git;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Exercises <see cref="ScriptHistoryService"/> against a real throwaway git repository laid out
/// exactly like an Obsync workspace (workspacesRoot\{profileId:N}) — no network required. Follows
/// the GitWorkspaceTests pattern: skip gracefully when the git CLI is unavailable.
/// </summary>
public sealed class ScriptHistoryServiceTests : IDisposable
{
    private const string RelativePath = "procedures/dbo.usp_GetCustomer.sql";
    private const string FirstVersion = "CREATE PROCEDURE dbo.usp_GetCustomer AS\nSELECT 1;";
    private const string SecondVersion = "CREATE PROCEDURE dbo.usp_GetCustomer AS\nSELECT 2;";

    private readonly string _workspacesRoot;
    private readonly GitCommandRunner _runner = new(NullLogger<GitCommandRunner>.Instance);

    public ScriptHistoryServiceTests()
    {
        _workspacesRoot = Path.Combine(Path.GetTempPath(), "obsync-scripthistory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacesRoot);
    }

    [Fact]
    public async Task Modified_ReturnsThePreviousAndNewContent()
    {
        if (!GitAvailable())
        {
            return;
        }

        var (repository, _, secondSha) = await InitWorkspaceAsync();
        var service = new ScriptHistoryService(_runner, _workspacesRoot);

        var result = await service.GetVersionsAsync(repository, secondSha, RelativePath, ChangeType.Modified);

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.Equal(FirstVersion, Normalize(result.OldContent));
        Assert.Equal(SecondVersion, Normalize(result.NewContent));
    }

    [Fact]
    public async Task Added_TreatsTheMissingParentVersionAsEmpty()
    {
        if (!GitAvailable())
        {
            return;
        }

        var (repository, firstSha, _) = await InitWorkspaceAsync();
        var service = new ScriptHistoryService(_runner, _workspacesRoot);

        var result = await service.GetVersionsAsync(repository, firstSha, RelativePath, ChangeType.Added);

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.Equal(string.Empty, result.OldContent);
        Assert.Equal(FirstVersion, Normalize(result.NewContent));
    }

    [Fact]
    public async Task Modified_AtTheFirstCommit_DegradesToAnEmptyOldVersion()
    {
        if (!GitAvailable())
        {
            return;
        }

        var (repository, firstSha, _) = await InitWorkspaceAsync();
        var service = new ScriptHistoryService(_runner, _workspacesRoot);

        // The first commit has no parent; a Modified lookup must not fail, just report no old content.
        var result = await service.GetVersionsAsync(repository, firstSha, RelativePath, ChangeType.Modified);

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.Equal(string.Empty, result.OldContent);
        Assert.Equal(FirstVersion, Normalize(result.NewContent));
    }

    [Fact]
    public async Task CommitMissingLocally_ReportsUnavailableWithAReason()
    {
        if (!GitAvailable())
        {
            return;
        }

        var (repository, _, _) = await InitWorkspaceAsync();
        var service = new ScriptHistoryService(_runner, _workspacesRoot);

        var result = await service.GetVersionsAsync(
            repository, new string('0', 40), RelativePath, ChangeType.Modified);

        Assert.False(result.IsAvailable);
        Assert.Contains("isn't present", result.UnavailableReason);
    }

    [Fact]
    public async Task MissingWorkspace_ReportsUnavailableWithAReason()
    {
        var neverSynced = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "r" };
        var service = new ScriptHistoryService(_runner, _workspacesRoot);

        var result = await service.GetVersionsAsync(
            neverSynced, new string('a', 40), RelativePath, ChangeType.Modified);

        Assert.False(result.IsAvailable);
        Assert.Contains("hasn't been synced", result.UnavailableReason);
    }

    /// <summary>Creates a workspace clone for a fresh profile with two commits touching one file.</summary>
    private async Task<(GitRepositoryProfile Repository, string FirstSha, string SecondSha)> InitWorkspaceAsync()
    {
        var repository = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "r" };
        var workspace = Path.Combine(_workspacesRoot, repository.Id.ToString("N"));
        Directory.CreateDirectory(workspace);

        await RunGitAsync(workspace, "init", "--initial-branch=main");
        await RunGitAsync(workspace, "config", "user.name", "Obsync Tests");
        await RunGitAsync(workspace, "config", "user.email", "tests@obsync.local");

        var filePath = Path.Combine(workspace, "procedures", "dbo.usp_GetCustomer.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        await File.WriteAllTextAsync(filePath, FirstVersion);
        await RunGitAsync(workspace, "add", "-A");
        await RunGitAsync(workspace, "commit", "-m", "first");
        var firstSha = (await RunGitAsync(workspace, "rev-parse", "HEAD")).Trim();

        await File.WriteAllTextAsync(filePath, SecondVersion);
        await RunGitAsync(workspace, "add", "-A");
        await RunGitAsync(workspace, "commit", "-m", "second");
        var secondSha = (await RunGitAsync(workspace, "rev-parse", "HEAD")).Trim();

        return (repository, firstSha, secondSha);
    }

    private async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await _runner.RunAsync(workingDirectory, arguments);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    // The runner reassembles process output line by line, so content comes back with platform line
    // endings and a trailing newline; normalize both sides for comparison.
    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n');

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspacesRoot))
            {
                // Git marks pack files read-only; clear attributes so the tree can be removed.
                foreach (var file in Directory.EnumerateFiles(_workspacesRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_workspacesRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp workspace.
        }
    }
}
