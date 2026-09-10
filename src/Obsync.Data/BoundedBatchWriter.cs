using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Obsync.Data;

/// <summary>
/// Runs one large multi-row write as a SEQUENCE of bounded transactions instead of a single
/// transaction spanning the whole batch.
/// </summary>
/// <remarks>
/// The database is opened in WAL mode (<see cref="SqliteConnectionFactory"/>), so a long write
/// transaction never blocks READERS — the app's lists and counts keep rendering throughout. What it
/// blocks is every other WRITER: a second job's persistence, the scheduler recording a run, the CLI,
/// the audit log. Those wait on <c>PRAGMA busy_timeout</c> (<see cref="ObsyncDataOptions"/>,
/// 30 s) and then fail with SQLITE_BUSY. A VLDB first run persists hundreds of thousands of rows in
/// one batch, which is long enough for that to be a real outcome rather than a theoretical one.
/// <para>
/// Committing every <see cref="TransactionRows"/> rows releases the write lock periodically so a
/// waiting writer gets in at a boundary instead of queueing behind the entire batch.
/// </para>
/// <para>
/// Choosing N. Each commit costs one WAL fsync (SQLite's default <c>synchronous</c> is FULL, which
/// this codebase does not override), so a small N multiplies fsyncs across the batch; a large N
/// holds the lock proportionally longer between chances to hand it over. At 5,000 rows a
/// 500,000-row batch commits 100 times, which puts the segment at roughly the same order as a
/// waiting writer's backoff (see below) for 99 extra fsyncs against a write already doing hundreds
/// of thousands of index updates. That is the balance point; an order of magnitude either way makes
/// one of the two costs dominate.
/// </para>
/// <para>
/// The boundary is tested with <c>&gt;=</c> AFTER a whole statement, so a transaction is always a
/// whole number of multi-row statements and never splits one. The effective segment is therefore
/// the smallest multiple of the caller's statement size that reaches N (25 × 200 for object-state
/// upserts, 15 × 350 for run changes, 10 × 500 for state deletes).
/// </para>
/// <para>
/// The pause at each boundary is the part that makes the rest of this worth doing, and it is not
/// obvious. SQLite's write lock is an OS file lock, not a fair queue: a connection that loses the
/// race backs off through the busy handler's schedule (1, 2, 5 … 100 ms) and retries. Committing
/// and immediately issuing the next <c>BEGIN IMMEDIATE</c> leaves the lock free for microseconds,
/// so a waiter sleeping out a backoff essentially never lands in the gap — the batch reacquires
/// every time and the boundaries are a formality.
/// </para>
/// <para>
/// That is measured, not assumed. With the pause removed, the competing writer in
/// <c>BoundedTransactionTests</c> queued behind the ENTIRE batch and took the lock 29-130 ms AFTER
/// it finished — exactly what it would have done under one batch-wide transaction. Committing
/// without pausing looks like a fix and delivers nothing.
/// </para>
/// <para>
/// The pause is a FRACTION of the segment that just ran, not a fixed duration, because what decides
/// whether a waiter gets in is the duty cycle — the share of wall-clock time the lock is free — and
/// segment duration varies by two orders of magnitude across machines. A waiter's backoff caps at
/// 100 ms, so at a duty cycle of <c>d</c> it expects to wait about <c>100 ms / d</c>; a quarter of
/// the segment (d ≈ 0.2) puts that under a second, against a batch it would otherwise sit out
/// entirely. A fixed pause cannot do this: 5 ms is a fifth of a fast machine's 100 ms segment but a
/// four-hundredth of a slow one's, where it made this project's own test fail one run in three.
/// </para>
/// <para>
/// The cost is a quarter of the persistence phase, and only for batches large enough to cross a
/// boundary. That phase is small to begin with: the audit's 50,202-object benchmark run spent 460 s
/// scripting and 126 s on the first commit out of 588 s total, leaving well under a second for all
/// of this. Adding 25% to the smallest term is not a trade worth protecting.
/// </para>
/// <para>
/// CORRECTNESS. Splitting one transaction into several means an interrupted process (crash, power
/// loss, kill) can leave PART of the batch durable. That is only acceptable because each caller's
/// rows are independent of one another AND partial persistence leaves the database strictly BEHIND
/// what was already delivered, never ahead of it — the per-method reasoning is recorded at each call
/// site. Do not use this helper for a batch whose rows must be observed together.
/// </para>
/// </remarks>
internal static class BoundedBatchWriter
{
    /// <summary>Rows after which the current transaction is committed and a new one begun.</summary>
    internal const int TransactionRows = 5_000;

    /// <summary>Share of a segment's own duration to leave the write lock free after committing it.</summary>
    private const int BoundaryPauseDivisor = 4;

    /// <summary>
    /// Floor and ceiling on that pause. The floor keeps a trivially fast segment from producing a
    /// gap too short for the OS scheduler to be meaningful; the ceiling keeps a pathologically slow
    /// one (a contended or failing disk) from turning the batch into mostly waiting.
    /// </summary>
    private static readonly TimeSpan MinBoundaryPause = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxBoundaryPause = TimeSpan.FromMilliseconds(500);

    private static TimeSpan BoundaryPause(TimeSpan segment) => TimeSpan.FromTicks(
        Math.Clamp(segment.Ticks / BoundaryPauseDivisor, MinBoundaryPause.Ticks, MaxBoundaryPause.Ticks));

    /// <param name="statementRows">
    /// Rows per multi-row statement — the caller's parameter-limit budget, unchanged by this helper.
    /// </param>
    /// <param name="buildStatement">
    /// Produces the SQL and parameters for one chunk. Called once per statement, inside the
    /// transaction the statement will run in.
    /// </param>
    internal static async Task ExecuteAsync<T>(
        SqliteConnection connection,
        IEnumerable<T> items,
        int statementRows,
        Func<T[], (string Sql, object Parameters)> buildStatement,
        CancellationToken cancellationToken)
    {
        // Begun lazily, and left null after each commit, so the batch never opens a transaction it
        // has no rows for — Microsoft.Data.Sqlite issues BEGIN IMMEDIATE, which takes the write lock
        // at BEGIN rather than at the first statement.
        DbTransaction? transaction = null;
        try
        {
            var rowsInTransaction = 0;
            var segmentStart = 0L;
            foreach (var chunk in items.Chunk(statementRows))
            {
                if (transaction is null)
                {
                    transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    segmentStart = Stopwatch.GetTimestamp();
                }

                var (sql, parameters) = buildStatement(chunk);
                await connection.ExecuteAsync(new CommandDefinition(
                    sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

                rowsInTransaction += chunk.Length;
                if (rowsInTransaction < TransactionRows)
                {
                    continue;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
                transaction = null;
                rowsInTransaction = 0;

                // Hold nothing across the pause: the commit above released the lock and the next
                // segment does not begin until this returns.
                await Task.Delay(BoundaryPause(Stopwatch.GetElapsedTime(segmentStart)), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Disposing a committed transaction is a no-op; disposing an uncommitted one rolls back
            // the segment in flight, which is exactly the boundary the callers reason about.
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
