using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.Repositories;
using Obsync.Engine;
using Obsync.Engine.DependencyInjection;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.E2E;

/// <summary>
/// One fully isolated Obsync engine environment: a temporary state database, a temporary
/// workspaces root, and a local bare git repository standing in for GitHub. Mirrors the
/// production composition (AddObsyncCore) exactly, so runs exercise the real pipeline.
/// </summary>
internal sealed class E2eEnvironment : IAsyncDisposable
{
    public ServiceProvider Provider { get; }
    public string Root { get; }
    public string WorkspacesRoot { get; }
    public string RemotePath { get; }
    public SqlConnectionProfile Connection { get; }
    public GitRepositoryProfile Repository { get; }

    private E2eEnvironment(ServiceProvider provider, string root, string workspaces, string remote,
        SqlConnectionProfile connection, GitRepositoryProfile repository)
    {
        Provider = provider;
        Root = root;
        WorkspacesRoot = workspaces;
        RemotePath = remote;
        Connection = connection;
        Repository = repository;
    }

    public static async Task<E2eEnvironment> CreateAsync(string parentRoot, string name, string sqlServer)
    {
        var root = Path.Combine(parentRoot, name);
        var workspaces = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspaces);

        var remote = GitTool.CreateSeededBareRepository(Path.Combine(root, "remote.git"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICredentialStore>(new StubCredentialStore());
        services.AddObsyncCore(Path.Combine(root, "state.db"), o => o.WorkspacesRoot = workspaces);
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile
        {
            Name = $"e2e-{name}",
            ServerName = sqlServer,
            TrustServerCertificate = true,
        };
        await provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);

        var repository = new GitRepositoryProfile
        {
            Name = $"e2e-{name}",
            Owner = "e2e",
            RepositoryName = name,
            RemoteUrl = remote,
        };
        await provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repository);

        return new E2eEnvironment(provider, root, workspaces, remote, connection, repository);
    }

    public async Task<SyncJob> AddJobAsync(Action<SyncJob> configure)
    {
        var job = new SyncJob
        {
            ConnectionProfileId = Connection.Id,
            RepositoryProfileId = Repository.Id,
            Branch = "main",
            CommitMode = Obsync.Shared.CommitMode.DirectCommit,
        };
        configure(job);
        await Provider.GetRequiredService<IJobRepository>().UpsertAsync(job);
        return job;
    }

    public async Task<SyncRun> RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var engine = Provider.GetRequiredService<ISyncEngine>();
        return await engine.RunJobAsync(jobId, Obsync.Shared.RunTrigger.Manual, progress: null, cancellationToken);
    }

    /// <summary>The local clone the engine created for this environment's repository (null until first git run).</summary>
    public string? FindWorkspaceClone()
    {
        if (!Directory.Exists(WorkspacesRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(WorkspacesRoot)
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, ".git")));
    }

    // ---- Remote (bare repo) inspection helpers --------------------------------------------------

    public int RemoteCommitCount(string branch = "main")
    {
        var output = GitTool.Capture(RemotePath, "rev-list", "--count", branch);
        return int.Parse(output.Trim());
    }

    public string RemoteHeadSha(string branch = "main") =>
        GitTool.Capture(RemotePath, "rev-parse", branch).Trim();

    public string RemoteTreeHash(string branch = "main") =>
        GitTool.Capture(RemotePath, "rev-parse", $"{branch}^{{tree}}").Trim();

    public IReadOnlyList<string> RemoteFiles(string branch = "main") =>
        GitTool.Capture(RemotePath, "ls-tree", "-r", "--name-only", branch)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>File content at HEAD of the bare remote; empty string when the path doesn't exist (a failed check, not a crash).</summary>
    public string RemoteFileContent(string path, string branch = "main")
    {
        try
        {
            return GitTool.Capture(RemotePath, "show", $"{branch}:{path}");
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<string> RemoteCommitFiles(string sha) =>
        GitTool.Capture(RemotePath, "diff-tree", "--no-commit-id", "--name-status", "-r", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Installs a pre-receive hook on the bare remote that rejects every push.</summary>
    public void RejectPushes()
    {
        var hooks = Path.Combine(RemotePath, "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-receive"), "#!/bin/sh\necho \"E2E: pushes rejected\" >&2\nexit 1\n");
    }

    public void AllowPushes()
    {
        var hook = Path.Combine(RemotePath, "hooks", "pre-receive");
        if (File.Exists(hook))
        {
            File.Delete(hook);
        }
    }

    /// <summary>Commits a foreign file to the bare remote via a throwaway clone (simulates another writer).</summary>
    public void PushForeignCommit(string relativePath, string content)
    {
        var cloneDir = Path.Combine(Root, $"foreign-{Guid.NewGuid():N}");
        GitTool.Capture(Root, "clone", RemotePath, cloneDir);
        var filePath = Path.Combine(cloneDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
        GitTool.Capture(cloneDir, "add", ".");
        GitTool.Capture(cloneDir, "-c", "user.name=foreign", "-c", "user.email=foreign@localhost", "commit", "-m", "foreign change");
        GitTool.Capture(cloneDir, "push", "origin", "HEAD:main");
    }

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
}

/// <summary>The engine demands a token for the git modes; a local bare remote never reads it.</summary>
internal sealed class StubCredentialStore : ICredentialStore
{
    public void Store(string key, string secret) { }
    public string? Retrieve(string key) => "e2e-local-token";
    public void Delete(string key) { }
    public bool Exists(string key) => true;
}

internal static class GitTool
{
    public static string CreateSeededBareRepository(string barePath)
    {
        var seed = barePath + ".seed";
        Capture(Path.GetTempPath(), "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "e2e seed\n");
        Capture(seed, "add", ".");
        Capture(seed, "-c", "user.name=e2e", "-c", "user.email=e2e@localhost", "commit", "-m", "seed");
        Capture(Path.GetTempPath(), "clone", "--bare", seed, barePath);
        return barePath;
    }

    public static string Capture(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        // quotepath=off: emit unicode paths verbatim instead of octal-escaped, so checks can match.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }
}
