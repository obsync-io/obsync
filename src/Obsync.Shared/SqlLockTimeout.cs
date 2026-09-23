namespace Obsync.Shared;

/// <summary>Converts the job's configured lock timeout into the milliseconds SQL Server expects.</summary>
public static class SqlLockTimeout
{
    /// <summary>
    /// Returns the <c>SET LOCK_TIMEOUT</c> argument for <paramref name="seconds"/>, saturating at
    /// <see cref="int.MaxValue"/> instead of overflowing.
    /// </summary>
    /// <remarks>
    /// The obvious <c>seconds * 1000</c> is unchecked int arithmetic, so any value above 2,147,483
    /// seconds (about 24.8 days) wraps negative: a configured or imported 3,000,000 produced
    /// <c>SET LOCK_TIMEOUT -1294967296</c>. SQL Server rejects every negative value except -1, so
    /// the session setup threw on each metadata connection and every database in the job failed —
    /// the exact inverse of the fail-fast intent. Saturating is also the closest honest reading of
    /// the request: int.MaxValue milliseconds is SQL Server's own maximum wait.
    /// <para>
    /// Three call sites need this (the metadata session and both SMO providers), which is why it
    /// lives here rather than being fixed once where it was first noticed.
    /// </para>
    /// </remarks>
    public static int ToMilliseconds(int seconds) =>
        seconds > int.MaxValue / 1000 ? int.MaxValue : seconds * 1000;
}
