using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Obsync.Git;

/// <summary>The result of running a single git command.</summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs the git CLI as a child process, capturing output without a shell.</summary>
public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitCommandRunner" />
public sealed class GitCommandRunner : IGitCommandRunner
{
    private static readonly Lazy<string> ResolvedGitExecutable = new(() => ResolveGitExecutable());

    private readonly ILogger<GitCommandRunner> _logger;

    public GitCommandRunner(ILogger<GitCommandRunner> logger) => _logger = logger;

    /// <summary>
    /// The git executable every command runs with, resolved once per process:
    /// (1) the <c>OBSYNC_GIT</c> environment variable, when set and pointing at an existing file;
    /// (2) the MinGit bundled by the installer at <c>tools\git\cmd\git.exe</c> next to the app;
    /// (3) plain <c>"git"</c> from <c>PATH</c> (dev machines and non-MSI installs).
    /// </summary>
    public static string GitExecutable => ResolvedGitExecutable.Value;

    /// <summary>Resolution behind <see cref="GitExecutable"/>; separate so tests can exercise each branch.</summary>
    internal static string ResolveGitExecutable(string? baseDirectory = null)
    {
        var overridePath = Environment.GetEnvironmentVariable("OBSYNC_GIT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var bundled = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "tools", "git", "cmd", "git.exe");
        return File.Exists(bundled) ? bundled : "git";
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Never block on an interactive credential prompt.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start the git process ({GitExecutable}). Is git installed and on PATH?");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var result = new GitCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (!result.Success)
        {
            // Redacted args: tokens are passed via http.extraheader values which we never log.
            _logger.LogDebug("git {Args} exited {Code}", string.Join(' ', RedactArguments(arguments)), result.ExitCode);
        }

        return result;
    }

    private static IEnumerable<string> RedactArguments(IReadOnlyList<string> arguments) =>
        arguments.Select(a =>
            a.StartsWith("http.extraheader=", StringComparison.OrdinalIgnoreCase) ? "http.extraheader=***" :
            a.StartsWith("http.proxy=", StringComparison.OrdinalIgnoreCase) ? "http.proxy=***" :
            a);
}
