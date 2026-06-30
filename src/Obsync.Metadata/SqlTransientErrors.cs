using Microsoft.Data.SqlClient;

namespace Obsync.Metadata;

/// <summary>
/// Classifies transient SQL failures worth retrying (deadlocks, lock timeouts, transport blips,
/// connection resets) and provides a bounded retry helper. Ported from the reference engine's
/// transient-error handling. Permanent failures (permission denied, encrypted object) are not
/// treated as transient.
/// </summary>
public static class SqlTransientErrors
{
    // SQL error numbers considered transient.
    private static readonly HashSet<int> TransientNumbers =
    [
        -2,     // Timeout expired
        20,     // Instance failure / encryption
        64,     // Connection was successfully established then failed
        233,    // Connection init error
        1205,   // Deadlock victim
        1222,   // Lock request time out
        4060,   // Cannot open database (transient at failover)
        10053,  // Transport-level error (forcibly closed)
        10054,  // Connection reset by peer
        10060,  // Network / timeout
        40197,  // Service error processing request
        40501,  // Service busy
        40613,  // Database unavailable (failover)
    ];

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)),
        TimeoutException => true,
        // Unwrap wrappers (e.g. SMO's ConnectionFailureException) that carry a transient SqlException inside.
        _ => exception.InnerException is not null && IsTransient(exception.InnerException),
    };

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying on transient failures with a short, growing
    /// backoff. Deadlocks/lock timeouts back off fast; connection errors back off progressively.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                var delayMs = IsLockContention(ex) ? 500 : 1000 * attempt;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(Exception exception) =>
        exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is 1205 or 1222);
}
