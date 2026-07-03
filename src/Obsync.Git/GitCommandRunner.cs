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
    private readonly ILogger<GitCommandRunner> _logger;

    public GitCommandRunner(ILogger<GitCommandRunner> logger) => _logger = logger;

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
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
            throw new InvalidOperationException("Failed to start the git process. Is git installed and on PATH?");
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
