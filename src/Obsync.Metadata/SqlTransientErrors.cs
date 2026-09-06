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

    /// <summary>
    /// SQL error numbers meaning "this database cannot be opened right now" for a reason that says
    /// nothing about the data and everything about the database's state.
    /// </summary>
    private static readonly HashSet<int> UnavailableDatabaseNumbers =
    [
        922,    // Database is being recovered
        927,    // Database cannot be opened — it is in the middle of a restore
        941,    // Database cannot be opened — it is not in a state that allows access
        942,    // Database cannot be opened — it is offline
        945,    // Database cannot be opened due to inaccessible files or insufficient memory/disk
        4060,   // Cannot open the database requested by the login
        40613,  // Database is currently unavailable (failover)
    ];

    /// <summary>
    /// Whether the failure means one DATABASE is unavailable, as opposed to something transient
    /// about the connection.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IsTransient"/> because the two answer different questions and
    /// only one of them is about retrying. A database mid-restore is not going to become available
    /// within a retry's backoff, so retrying it just burns the attempts — but the RUN should still
    /// contain the failure to that database and commit everything else, exactly as it already does
    /// for an offline one.
    /// <para>
    /// Before this existed, only 4060 and 40613 were listed (as transient), so an offline database
    /// was contained while a RECOVERING one — operationally identical from the user's point of view,
    /// and the normal state for a few minutes after a failover — escaped the loop, skipped
    /// FinalizeAsync, and discarded every other database's completed work with nothing committed.
    /// </para>
    /// </remarks>
    public static bool IsUnavailableDatabase(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => UnavailableDatabaseNumbers.Contains(e.Number)),
        _ => exception.InnerException is not null && IsUnavailableDatabase(exception.InnerException),
    };

    /// <summary>
    /// Whether a run may continue past this failure by containing it to the current database —
    /// either a transient blip or an unavailable database.
    /// </summary>
    public static bool IsContainable(Exception exception) =>
        IsTransient(exception) || IsUnavailableDatabase(exception);

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
