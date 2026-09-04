using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Obsync.Git;
using Obsync.Shared.Results;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// Obsync ships its own git, which reads as isolation but is not: the bundled system config ends
/// with an <c>include.path</c> of Git for Windows' machine config, so machine-wide settings apply in
/// full. Several of them run programs — <c>core.hooksPath</c> and <c>core.fsmonitor</c> on this
/// product's own commit and add, and an <c>ext::</c> URL rewritten in by <c>url.*.insteadOf</c>.
///
/// These run the REAL git the product runs, and assert the settings it actually sees, because the
/// whole point is what the child process resolves rather than what we believe we passed it.
/// </summary>
public sealed class GitHardeningTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"obsync-hardening-{Guid.NewGuid():N}");

    private readonly GitCommandRunner _runner = new(NullLogger<GitCommandRunner>.Instance);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var init = await _runner.RunAsync(_root, ["init", "-q", "--initial-branch=main", "repo"]);
        Assert.True(init.Success, init.StandardError);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private string Repo => Path.Combine(_root, "repo");

    private async Task<string> ConfigAsync(string key)
    {
        var result = await _runner.RunAsync(Repo, ["config", "--get", key]);
        return result.StandardOutput.Trim();
    }

    [Fact]
    public async Task EveryInvocation_RunsWithNoHooks()
    {
        // core.hooksPath is pointed at a path that does not exist, which is how git is told to run
        // no hooks at all. Verified by execution elsewhere; here we pin that git resolves it.
        var hooksPath = await ConfigAsync("core.hooksPath");

        Assert.NotEmpty(hooksPath);
        Assert.False(Directory.Exists(hooksPath), $"the no-hooks path must not exist: {hooksPath}");
    }

    [Fact]
    public async Task EveryInvocation_DisablesTheFileSystemMonitorAndCredentialHelper()
    {
        // fsmonitor runs a program on `add`. The credential helper is cleared unconditionally now:
        // the bundled config ships credential.helper=manager, and a token-less run used to fall
        // through to it because the clearing sat inside an "if a token exists" branch.
        Assert.Equal("false", await ConfigAsync("core.fsmonitor"));
        Assert.Equal(string.Empty, await ConfigAsync("credential.helper"));
        Assert.Equal(string.Empty, await ConfigAsync("core.askPass"));
    }

    [Fact]
    public async Task EveryInvocation_AllowsOnlyTheTransportsThisProductUses()
    {
        // ext:: executes an arbitrary command and ssh runs core.sshCommand; either can be reached
        // from a machine-config url.*.insteadOf rewrite. file stays allowed because a plain local
        // path is a legitimate remote.
        Assert.Equal("never", await ConfigAsync("protocol.allow"));
        Assert.Equal("never", await ConfigAsync("protocol.ext.allow"));
        Assert.Equal("never", await ConfigAsync("protocol.ssh.allow"));
        Assert.Equal("always", await ConfigAsync("protocol.https.allow"));
        Assert.Equal("always", await ConfigAsync("protocol.http.allow"));
        Assert.Equal("always", await ConfigAsync("protocol.file.allow"));
    }

    [Fact]
    public async Task ConfigCannotReEnableATransportThatExecutesACommand()
    {
        // The load-bearing property: the whole finding is that settings from config Obsync does not
        // own reach its git. `ext::` runs an arbitrary command and is how a url.*.insteadOf rewrite
        // becomes code execution. Here a config git genuinely reads turns it back on — and the forced
        // configuration must still win, because GIT_CONFIG_* is applied last.
        var enable = await _runner.RunAsync(Repo, ["config", "--local", "protocol.ext.allow", "always"]);
        Assert.True(enable.Success, enable.StandardError);
        Assert.Equal("always", (await _runner.RunAsync(Repo, ["config", "--local", "--get", "protocol.ext.allow"]))
            .StandardOutput.Trim());

        var result = await _runner.RunAsync(Repo, ["ls-remote", "ext::sh -c whoami"]);

        Assert.False(result.Success);
        Assert.Contains("transport 'ext' not allowed", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigCannotReEnableHooks()
    {
        // Same precedence check for the key that executes on this product's own commit.
        var hooks = Path.Combine(_root, "planted-hooks");
        Directory.CreateDirectory(hooks);
        Assert.True((await _runner.RunAsync(Repo, ["config", "--local", "core.hooksPath", hooks])).Success);

        var effective = (await _runner.RunAsync(Repo, ["config", "--get", "core.hooksPath"])).StandardOutput.Trim();

        Assert.NotEqual(hooks, effective);
        Assert.False(Directory.Exists(effective));
    }

    [Fact]
    public async Task APlainLocalPathRemote_StillWorks()
    {
        // The other side of the allow-list: local paths are the 'file' transport, and blocking them
        // would break local/UNC remotes (and this suite). This is the regression that guards it.
        var origin = Path.Combine(_root, "origin.git");
        Assert.True((await _runner.RunAsync(_root, ["init", "--bare", "-q", "--initial-branch=main", origin])).Success);

        var clone = await _runner.RunAsync(_root, ["clone", "-q", origin, Path.Combine(_root, "clone")]);

        Assert.True(clone.Success, clone.StandardError);
    }

    [Fact]
    public async Task TheAuthHeaderIsSentUnderAKeyScopedToTheRemote()
    {
        // The helper computing the prefix is tested above; this pins that the workspace actually
        // USES it. An unscoped http.extraheader is the whole finding — it applies to whatever host
        // the command reaches, including one substituted by a url.*.insteadOf rewrite.
        IReadOnlyDictionary<string, string>? captured = null;
        var runner = Substitute.For<IGitCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, string.Empty, string.Empty));
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Do<IReadOnlyDictionary<string, string>?>(e => captured ??= e), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, string.Empty, string.Empty));

        var workspace = new GitWorkspace(runner, NullLogger<GitWorkspace>.Instance);
        await workspace.PushAsync(new GitWorkspaceContext
        {
            RemoteUrl = "https://github.com/acme/repo.git",
            Branch = "main",
            LocalPath = Repo,
            AuthorizationHeader = "AUTHORIZATION: basic ZmFrZQ==",
        });

        Assert.NotNull(captured);
        var keys = captured!.Where(kv => kv.Key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal))
            .Select(kv => kv.Value).ToList();
        Assert.Contains("http.https://github.com/.extraheader", keys);
        Assert.DoesNotContain("http.extraheader", keys);
    }

    [Theory]
    [InlineData("https://github.com/acme/repo.git", "https://github.com/")]
    [InlineData("http://ghe.corp.local/team/db.git", "http://ghe.corp.local/")]
    [InlineData("https://ghe.corp.local:8443/team/db.git", "https://ghe.corp.local:8443/")]
    [InlineData("https://github.com", "https://github.com/")]
    public void HttpScopePrefix_IsTheOriginOfTheRemote(string remote, string expected)
    {
        // The auth header is scoped to this prefix. git matches http.<url>.* by longest URL prefix,
        // so the origin covers every path under it and nothing on another host.
        Assert.Equal(expected, GitWorkspace.HttpScopePrefix(remote));
    }

    [Fact]
    public void HttpScopePrefix_KeepsAnExplicitlyWrittenDefaultPort()
    {
        // Rebuilding from a parsed Uri drops ":443" as redundant. If git matches ports strictly the
        // scoped key would then never match the remote, the header would not be sent, and the push
        // would fail to authenticate — a silent auth break in the name of hardening. The prefix is
        // sliced from the original string so it always matches literally.
        Assert.Equal("https://github.com:443/", GitWorkspace.HttpScopePrefix("https://github.com:443/acme/repo.git"));
    }

    [Fact]
    public void HttpScopePrefix_IsAlwaysAPrefixOfTheRemoteItScopes()
    {
        // The property that makes the scope correct by construction, whatever either side normalizes.
        foreach (var remote in new[]
        {
            "https://github.com/acme/repo.git",
            "https://github.com:443/acme/repo.git",
            "http://ghe.corp.local:8080/team/db.git",
        })
        {
            Assert.StartsWith(GitWorkspace.HttpScopePrefix(remote)!, remote, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\workspaces\origin.git")]
    [InlineData("ssh://git@github.com/acme/repo.git")]
    public void HttpScopePrefix_IsNullWhenThereIsNoHttpRemoteToScopeTo(string? remote)
    {
        // No HTTP remote means an HTTP auth header is meaningless, so none is sent at all.
        Assert.Null(GitWorkspace.HttpScopePrefix(remote));
    }
}
