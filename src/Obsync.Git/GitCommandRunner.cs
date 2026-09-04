using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Obsync.Shared;

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

    /// <param name="environment">
    /// Extra environment variables for the child process. Secrets (auth header, proxy credentials)
    /// travel here — environment blocks are not captured by Windows process-creation auditing
    /// (Event 4688 / Sysmon / EDR), unlike command lines, which those pipelines record verbatim.
    /// </param>
    Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitCommandRunner" />
public sealed class GitCommandRunner : IGitCommandRunner
{
    private static readonly Lazy<string> ResolvedGitExecutable = new(() => ResolveGitExecutable());

    /// <summary>
    /// How long a network command (clone/fetch/push) may run before Obsync terminates it. Generous:
    /// a first clone of a large estate is legitimately slow.
    /// </summary>
    private static readonly TimeSpan NetworkCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a local command may run. These are index and ref operations; minutes is already extreme.</summary>
    private static readonly TimeSpan LocalCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Exit code reported for a command Obsync terminated on timeout (matching GNU timeout).</summary>
    internal const int TimedOutExitCode = 124;

    /// <summary>
    /// A hooks directory that does not exist, which is how git is told to run no hooks at all. It
    /// sits inside the install directory: creating it requires the same privilege as replacing the
    /// binaries, so it cannot be used to re-enable hooks by anyone who could not already do worse.
    /// </summary>
    private static readonly string NoHooksPath = Path.Combine(AppContext.BaseDirectory, "obsync-hooks-disabled");

    /// <summary>
    /// Configuration forced on EVERY git invocation, overriding the machine's system and global
    /// gitconfig (GIT_CONFIG_* wins over both).
    ///
    /// Obsync ships its own git, which reads as isolation but is not: the bundled
    /// <c>tools/git/etc/gitconfig</c> ends with an <c>include.path</c> of Git for Windows' system
    /// config, so machine-wide settings apply to us in full. Several of those settings run programs
    /// — verified by execution, not inference: <c>core.hooksPath</c> and <c>core.fsmonitor</c> execute
    /// on this product's own commit and add; <c>filter.*</c> on checkout; and an
    /// <c>ext::</c> URL rewritten in by <c>url.*.insteadOf</c> executes an arbitrary command.
    ///
    /// The transport allow-list is what stops the last two: <c>ext</c> and <c>ssh</c> are refused, so
    /// a rewrite into either fails instead of running something. <c>file</c> stays allowed because a
    /// plain local path is a legitimate remote (and the git tests use one).
    ///
    /// Deliberately NOT full isolation: <c>GIT_CONFIG_NOSYSTEM</c> plus a private HOME would also
    /// close <c>filter.*</c>, but it would discard <c>core.autocrlf</c> and <c>core.symlinks</c> that
    /// the bundled config supplies — changing the line endings of every committed file on upgrade —
    /// along with any corporate CA bundle or proxy the site relies on.
    /// </summary>
    private static readonly (string Key, string Value)[] HardeningConfig =
    [
        // Always, not only when a token is present: a token-less run would otherwise fall through to
        // the machine's helper (the bundled config ships credential.helper=manager).
        ("credential.helper", string.Empty),
        ("core.hooksPath", NoHooksPath),
        ("core.fsmonitor", "false"),
        // Neither prompt helper may run: under the service there is no desktop to answer one, so it
        // blocks forever holding the job and repository locks. An expired token is enough to reach
        // this — git asks for credentials after a 401, however the request was authenticated.
        ("core.askPass", string.Empty),
        // Deny the two code-executing transports BY NAME, not only through the catch-all below:
        // `protocol.<name>.allow` is more specific than `protocol.allow`, so a config setting
        // `protocol.ext.allow=always` beats a catch-all `never` no matter who set it. Naming them
        // puts the deny on the same key, where being applied last is what decides it. Measured: with
        // only the catch-all, an ext:: remote still executed.
        ("protocol.ext.allow", "never"),
        ("protocol.ssh.allow", "never"),
        ("protocol.allow", "never"),
        ("protocol.https.allow", "always"),
        ("protocol.http.allow", "always"),
        ("protocol.file.allow", "always"),
    ];

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

    public Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, arguments, environment: null, cancellationToken);

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken = default)
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

        // ...which gates only the TERMINAL fallback. git consults askpass FIRST, so a helper
        // inherited from the parent environment still runs and can block indefinitely. Measured:
        // with GIT_ASKPASS set, a 401 blocks for as long as the helper lives; with it removed the
        // same request fails in about a second. VS Code sets GIT_ASKPASS in every terminal it opens,
        // so any host started from one inherits it.
        startInfo.Environment.Remove("GIT_ASKPASS");
        startInfo.Environment.Remove("SSH_ASKPASS");

        // Stable English output: transient-vs-permanent classification and the push-failure
        // explanations match on stderr text, which a localized PATH git would translate.
        startInfo.Environment["LC_ALL"] = "C";

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        ApplyHardeningConfig(startInfo, environment);

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

        // Nothing else bounds a git command. The engine's token is cancelled by a user pressing
        // Cancel or by the service stopping — never by a clock — so a git that blocks (a prompt
        // helper, a stalled connection) held the job AND repository locks indefinitely, and the run
        // row stayed "Running" because the orphan cleaner skips a run whose lock is still held.
        // Contending jobs then waited their full 30 minutes per occurrence, and with Quartz's
        // ten-thread pool enough of them starved jobs on unrelated repositories too.
        var timeout = environment is null ? LocalCommandTimeout : NetworkCommandTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our timeout, not the caller's cancellation: report a failed command rather than
            // throwing, so the run is recorded as Failed with a reason instead of Cancelled.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _logger.LogError(
                "git {Args} exceeded {Minutes} minutes and was terminated.",
                string.Join(' ', RedactArguments(arguments)), timeout.TotalMinutes);
            return new GitCommandResult(
                TimedOutExitCode,
                stdout.ToString(),
                stderr + $"obsync: git did not finish within {timeout.TotalMinutes:0} minutes and was terminated.");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var result = new GitCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (!result.Success)
        {
            _logger.LogDebug("git {Args} exited {Code}", string.Join(' ', RedactArguments(arguments)), result.ExitCode);
        }

        return result;
    }

    /// <summary>
    /// Appends <see cref="HardeningConfig"/> to whatever <c>GIT_CONFIG_*</c> entries the caller
    /// supplied, continuing their numbering so neither set overwrites the other, and rewrites
    /// <c>GIT_CONFIG_COUNT</c> to cover both.
    /// </summary>
    private static void ApplyHardeningConfig(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environment)
    {
        var index = 0;
        if (environment is not null
            && environment.TryGetValue("GIT_CONFIG_COUNT", out var supplied)
            && int.TryParse(supplied, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            index = count;
        }

        foreach (var (key, value) in HardeningConfig)
        {
            startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = key;
            startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = value;
            index++;
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] = index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Strips credentials from a logged command line. The two <c>-c</c> forms below are kept for
    /// older call paths, but the value that actually still reaches argv is the <b>remote URL</b> —
    /// clone and remote set-url take it positionally, so an operator who put credentials in a
    /// repository profile's RemoteUrl had them logged verbatim. The token itself never appears here:
    /// it travels as a GIT_CONFIG_* environment variable precisely so it stays off the command line.
    /// Internal for tests.
    /// </summary>
    internal static IEnumerable<string> RedactArguments(IReadOnlyList<string> arguments) =>
        arguments.Select(a =>
            a.StartsWith("http.extraheader=", StringComparison.OrdinalIgnoreCase) ? "http.extraheader=***" :
            a.StartsWith("http.proxy=", StringComparison.OrdinalIgnoreCase) ? "http.proxy=***" :
            SecretRedactor.Scrub(a) ?? a);
}
