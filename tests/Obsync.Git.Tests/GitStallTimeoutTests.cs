using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Obsync.Git;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// A network command is governed by whether it is still making progress, not by how long it has
/// been running. The two are the same only when the payload is roughly constant, which is exactly
/// what is not true for a product whose first run against a large estate delivers a million files.
///
/// The old flat ten-minute budget was wrong in both directions at once: a dead connection was
/// tolerated for ten minutes, and a healthy transfer with a lot to send was killed while it was
/// still working. These tests pin the replacement — and, just as importantly, that a stall is
/// classified retryable while an absolute-ceiling kill is not.
/// </summary>
public sealed class GitStallTimeoutTests
{
    private static readonly Dictionary<string, string> NetworkEnvironment =
        new() { ["GIT_CONFIG_COUNT"] = "0" };

    private static bool GitAvailable()
    {
        try
        {
            var probe = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);
            return probe.RunAsync(Path.GetTempPath(), ["--version"]).GetAwaiter().GetResult().Success;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task AConnectionThatGoesSilent_IsAbandonedOnTheStallBudget()
    {
        if (!GitAvailable())
        {
            return;
        }

        // A listener that completes the TCP handshake and then never answers. This is the shape the
        // flat budget handled worst: git is connected, so nothing errors, and it simply waits. No
        // external network is involved.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptTcpClientAsync().ConfigureAwait(false));
                }
            }
            catch (ObjectDisposedException) { /* listener stopped with the test */ }
            catch (SocketException) { /* listener stopped with the test */ }
        });

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance)
        {
            StallTimeout = TimeSpan.FromSeconds(3),
        };

        var started = DateTime.UtcNow;
        var result = await runner.RunAsync(
            Path.GetTempPath(),
            ["ls-remote", "--heads", "--", $"http://127.0.0.1:{port}/silent.git"],
            NetworkEnvironment,
            CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        listener.Stop();
        foreach (var client in accepted) { client.Dispose(); }
        await accepting;

        Assert.Equal(GitCommandRunner.TimedOutExitCode, result.ExitCode);
        Assert.Contains("produced no progress", result.StandardError, StringComparison.Ordinal);

        // The point of the change: abandoned on the stall budget, not held to an absolute ceiling.
        Assert.True(
            elapsed < TimeSpan.FromMinutes(1),
            $"a silent connection must be abandoned on the stall budget, not the ceiling; took {elapsed}.");
    }

    /// <summary>
    /// The half that made the old behaviour unrecoverable rather than merely slow. The kill message
    /// matched none of the transient markers, so <c>RunNetworkAsync</c>'s retry loop returned on
    /// attempt 1 and the configured retry count did nothing at all.
    /// </summary>
    [Fact]
    public void AStalledTransferIsRetryable_AndACeilingKillIsNot()
    {
        Assert.True(GitTransientErrors.IsTransient(
            "obsync: git produced no progress for 3 minutes and was terminated."));

        // Deliberately NOT transient: retrying an absolute-ceiling kill burns the same ceiling again
        // on the same doomed transfer.
        Assert.False(GitTransientErrors.IsTransient(
            "obsync: git did not finish within 120 minutes and was terminated."));
    }
}
