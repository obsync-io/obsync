using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Common;
using Obsync.Shared;
using Obsync.Shared.Scripting;
using SmoServer = Microsoft.SqlServer.Management.Smo.Server;

namespace Obsync.Smo;

/// <summary>
/// Shared SMO connection plumbing for the database- and server-level script providers: builds the
/// server-level <see cref="ServerConnection"/> from a script request and forces the initial
/// connect with a transient-failure retry.
/// </summary>
internal static class SmoConnection
{
    /// <summary>
    /// Disconnects <paramref name="server"/> when the scope ends, however it ends.
    ///
    /// An explicit <c>Connect()</c> pins the pooled connection open: SMO's own auto-return-to-pool
    /// is disabled once a caller connects deliberately, so the underlying connection stays checked
    /// out until someone disconnects. The slice workers already do this in a <c>finally</c>; the two
    /// primary connections did not, leaking one per database per run plus one for the server pass,
    /// and leaving those sessions open on the SQL Server for as long as they survived.
    ///
    /// A scope rather than a try/finally because both callers are <c>async IAsyncEnumerable</c>
    /// iterators — this disposes when the enumerator does, including on an early break.
    /// </summary>
    public static IDisposable Lease(SmoServer server, ILogger logger) => new ConnectionLease(server, logger);

    private sealed class ConnectionLease(SmoServer server, ILogger logger) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                server.ConnectionContext.Disconnect();
            }
            catch (Exception ex)
            {
                // Best-effort teardown; a failed disconnect must not mask the caller's outcome.
                logger.LogDebug("Disconnecting an SMO connection failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Builds an unconnected SMO server from the request's profile (no database — server-level).</summary>
    public static SmoServer BuildServer(ScriptRequest request)
    {
        var connection = new ServerConnection
        {
            ServerInstance = request.Profile.ServerName,
            EncryptConnection = request.Profile.Encrypt,
            TrustServerCertificate = request.Profile.TrustServerCertificate,
            StatementTimeout = request.CommandTimeoutSeconds,
            ConnectTimeout = request.Profile.ConnectTimeoutSeconds,
        };

        if (request.Profile.AuthenticationMode == SqlAuthenticationMode.SqlLogin)
        {
            connection.LoginSecure = false;
            connection.Login = request.Profile.Username ?? string.Empty;
            connection.Password = request.Password ?? string.Empty;
        }
        else
        {
            connection.LoginSecure = true;
        }

        return new SmoServer(connection);
    }

    /// <summary>
    /// Forces the SMO server connection up front, retrying transient failures (deadlocks, timeouts,
    /// transport/connection blips) with a short growing backoff. The initial connect is where nearly
    /// all transient SMO failures surface; per-object scripting failures are handled separately (as skips).
    /// </summary>
    public static async Task ConnectWithRetryAsync(
        SmoServer server, int maxRetries, ILogger logger, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, maxRetries);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                server.ConnectionContext.Connect();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                logger.LogWarning(
                    "Transient SMO connection failure (attempt {Attempt}/{Max}); retrying: {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // SQL error numbers worth retrying (transport blips, deadlocks, timeouts, failover). Kept local
    // to avoid coupling the SMO project to the metadata provider's retry helper.
    private static readonly HashSet<int> TransientSqlNumbers =
        [-2, 20, 64, 233, 1205, 1222, 4060, 10053, 10054, 10060, 40197, 40501, 40613];

    private static bool IsTransient(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Errors.Cast<SqlError>().Any(err => TransientSqlNumbers.Contains(err.Number)))
            {
                return true;
            }

            if (e is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
