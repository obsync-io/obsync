using Microsoft.Extensions.Logging.Abstractions;
using Obsync.Git;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// The hardening in <see cref="GitCommandRunner"/> is delivered through <c>GIT_CONFIG_KEY_n</c>, and
/// git applies several INHERITED environment variables that beat it. <c>ProcessStartInfo.Environment</c>
/// is seeded from the parent process, so anything set for the account running Obsync reaches git —
/// and setting a user environment variable requires no privilege at all, which makes it a strictly
/// easier attack than editing the machine gitconfig the hardening was written against.
///
/// These set the variables for real and run the real git, because the question is what the child
/// process resolves, not what we believe we passed it. Each restores the previous value, and the
/// assembly runs single-threaded (see AssemblyInfo.cs) so no sibling test observes them.
/// </summary>
public sealed class GitEnvironmentIsolationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"obsync-gitenv-{Guid.NewGuid():N}");
    private readonly GitCommandRunner _runner = new(NullLogger<GitCommandRunner>.Instance);
    private readonly List<(string Name, string? Previous)> _restore = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var init = await _runner.RunAsync(_root, ["init", "-q", "--initial-branch=main", "repo"]);
        Assert.True(init.Success, init.StandardError);

        var decoy = await _runner.RunAsync(_root, ["init", "-q", "--bare", "decoy.git"]);
        Assert.True(decoy.Success, decoy.StandardError);
    }

    public Task DisposeAsync()
    {
        foreach (var (name, previous) in _restore)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private string Repo => Path.Combine(_root, "repo");

    private void SetHostile(string name, string value)
    {
        _restore.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    private async Task<string> ConfigAsync(string key)
    {
        var result = await _runner.RunAsync(Repo, ["config", "--get", key]);
        return result.StandardOutput.Trim();
    }

    [Fact]
    public async Task GitConfigParameters_CannotReEnableACodeExecutingTransport()
    {
        // git reads GIT_CONFIG_PARAMETERS AFTER the numbered GIT_CONFIG_* block, so before the fix
        // this beat every hardening key and an `ext::` remote executed an arbitrary command.
        SetHostile("GIT_CONFIG_PARAMETERS", "'protocol.ext.allow=always' 'core.hooksPath=/pwned'");

        Assert.Equal("never", await ConfigAsync("protocol.ext.allow"));
        Assert.NotEqual("/pwned", await ConfigAsync("core.hooksPath"));

        var result = await _runner.RunAsync(Repo, ["ls-remote", "ext::sh -c whoami"]);
        Assert.False(result.Success);
        Assert.Contains("transport 'ext' not allowed", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitConfigParameters_CannotRestoreTheCredentialHelperOrPromptHelper()
    {
        // Both of these block forever under the service, holding the job and repository locks:
        // there is no desktop for a prompt to appear on.
        SetHostile("GIT_CONFIG_PARAMETERS", "'credential.helper=manager' 'core.askPass=/bin/echo'");

        Assert.Equal(string.Empty, await ConfigAsync("credential.helper"));
        Assert.Equal(string.Empty, await ConfigAsync("core.askPass"));
    }

    [Fact]
    public async Task GitDir_CannotRedirectCommandsAwayFromTheWorkingDirectory()
    {
        // WorkingDirectory does NOT protect against this: with GIT_DIR inherited, every add, commit
        // and checkout in GitWorkspace operated on a different repository while every path in the
        // product still looked correct.
        SetHostile("GIT_DIR", Path.Combine(_root, "decoy.git"));

        var result = await _runner.RunAsync(Repo, ["rev-parse", "--absolute-git-dir"]);

        Assert.True(result.Success, result.StandardError);
        Assert.DoesNotContain("decoy", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repo", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitAllowProtocol_CannotReplaceTheTransportAllowList()
    {
        // GIT_ALLOW_PROTOCOL substitutes for protocol.*.allow wholesale, which is the single control
        // stopping an ext:: rewrite from running a command.
        SetHostile("GIT_ALLOW_PROTOCOL", "ext:https:file");

        var result = await _runner.RunAsync(Repo, ["ls-remote", "ext::sh -c whoami"]);

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInheritedNumberedConfigBlock_DoesNotSurvive()
    {
        // The numbered keys cannot be cleared by name, so an inherited block is stripped wholesale
        // before this process builds its own. Left in place, a higher inherited GIT_CONFIG_COUNT
        // kept indexes our block never overwrites.
        SetHostile("GIT_CONFIG_COUNT", "1");
        SetHostile("GIT_CONFIG_KEY_0", "protocol.ext.allow");
        SetHostile("GIT_CONFIG_VALUE_0", "always");

        Assert.Equal("never", await ConfigAsync("protocol.ext.allow"));
    }
}
