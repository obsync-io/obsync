using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Obsync.Data;
using Obsync.Data.DependencyInjection;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Integration.Tests;

/// <summary>
/// The large batch writers commit every <see cref="BoundedBatchWriter.TransactionRows"/> rows
/// instead of once for the whole batch, so a VLDB-scale write releases the database's single write
/// lock periodically rather than holding it against every other writer for its full duration.
/// <para>
/// These tests cover the two things that has to be true: every row still lands when a batch spans
/// several transactions, and the lock really is obtainable at a boundary. The third test pins the
/// consequence that made this worth analysing — an interrupted batch leaves the segments it had
/// already committed, which is the partial state the engine's next run is relied on to repair.
/// </para>
/// </summary>
public sealed class BoundedTransactionTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"obsync-bounded-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private SyncJob _job = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddObsyncData(_dbPath);
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var connection = new SqlConnectionProfile { Name = "c", ServerName = "s" };
        var repo = new GitRepositoryProfile { Name = "r", Owner = "o", RepositoryName = "n" };
        await _provider.GetRequiredService<IConnectionProfileRepository>().UpsertAsync(connection);
        await _provider.GetRequiredService<IRepositoryProfileRepository>().UpsertAsync(repo);
        _job = new SyncJob { Name = "j", ConnectionProfileId = connection.Id, RepositoryProfileId = repo.Id };
        await _provider.GetRequiredService<IJobRepository>().UpsertAsync(_job);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertMany_SpanningTwoTransactionBoundaries_PersistsEveryRow()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        // Two full transaction segments, then a partial one that itself ends on a partial statement
        // — so the batch exercises both boundary kinds at once.
        var total = (BoundedBatchWriter.TransactionRows * 2) + ObjectStateRepository.UpsertChunkRows + 7;

        await states.UpsertManyAsync([.. Enumerable.Range(0, total).Select(i => State(i, $"hash{i}"))]);

        Assert.Equal(total, await states.CountForJobAsync(_job.Id));

        // Spot-check the rows either side of each commit point, not just the ends: a boundary that
        // dropped or duplicated its last statement would still leave the count plausible.
        var loaded = (await states.GetForJobDatabaseAsync(_job.Id, "Db1"))
            .ToDictionary(s => s.ObjectName, StringComparer.Ordinal);
        foreach (var i in new[]
        {
            0,
            BoundedBatchWriter.TransactionRows - 1, BoundedBatchWriter.TransactionRows,
            (BoundedBatchWriter.TransactionRows * 2) - 1, BoundedBatchWriter.TransactionRows * 2,
            total - 1,
        })
        {
            Assert.Equal($"hash{i}", loaded[$"usp_{i:D6}"].LastHash);
        }

        // Re-upsert across the same boundaries: the ON CONFLICT path has to update in place per
        // segment, not insert duplicates once a new transaction has started.
        await states.UpsertManyAsync([.. Enumerable.Range(0, total).Select(i => State(i, $"new{i}"))]);

        Assert.Equal(total, await states.CountForJobAsync(_job.Id));
        loaded = (await states.GetForJobDatabaseAsync(_job.Id, "Db1"))
            .ToDictionary(s => s.ObjectName, StringComparer.Ordinal);
        Assert.Equal("new0", loaded["usp_000000"].LastHash);
        Assert.Equal($"new{total - 1}", loaded[$"usp_{total - 1:D6}"].LastHash);
    }

    [Fact]
    public async Task DeleteMany_SpanningATransactionBoundary_RemovesEveryRow()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        var total = BoundedBatchWriter.TransactionRows + ObjectStateRepository.DeleteChunkIds + 3;
        await states.UpsertManyAsync([.. Enumerable.Range(0, total).Select(i => State(i, $"hash{i}"))]);

        var ids = (await states.GetForJobDatabaseAsync(_job.Id, "Db1")).Select(s => s.Id).ToList();
        Assert.Equal(total, ids.Count);

        await states.DeleteManyAsync(ids);

        Assert.Equal(0, await states.CountForJobAsync(_job.Id));
    }

    [Fact]
    public async Task AddChanges_SpanningATransactionBoundary_RoundTrips()
    {
        var runs = _provider.GetRequiredService<IRunRepository>();
        var run = await InsertRunAsync(runs);
        var total = BoundedBatchWriter.TransactionRows + RunRepository.ChangeChunkRows + 11;

        await runs.AddChangesAsync(run.Id, [.. Enumerable.Range(0, total).Select(i => new ObjectChange
        {
            ChangeType = ChangeType.Added,
            ObjectType = SqlObjectType.StoredProcedure,
            Schema = "dbo",
            Name = $"usp_{i:D6}",
            RelativePath = $"procedures/dbo.usp_{i:D6}.sql",
            NewHash = $"h{i:D6}",
        })]);

        var changes = await runs.GetChangesAsync(run.Id);
        Assert.Equal(total, changes.Count);
        Assert.Equal("usp_000000", changes[0].Name); // ordered by (change_type, schema, name)
        Assert.Equal($"usp_{total - 1:D6}", changes[^1].Name);
    }

    /// <summary>
    /// The correctness consequence of bounding, pinned rather than assumed: a batch that fails
    /// partway KEEPS the segments it had already committed, and loses only the segment in flight.
    ///
    /// This is the state SyncEngine's next run has to repair, and the reasoning for why it can is
    /// recorded on UpsertManyAsync: PersistStatesAsync writes these rows only after the changeset
    /// was delivered, so a committed row describes content the repository already holds; the
    /// watermarks that could let a later run SKIP an object are written after all of this, so a
    /// partial write leaves the database behind the repository rather than ahead of it; and the
    /// objects whose rows were lost are simply re-detected, rewritten byte-identically, and
    /// delivered by CommitAndPushAsync's "identical git tree" branch on the following run.
    ///
    /// The failure is injected with a NOT NULL violation (last_hash) rather than a real crash
    /// because it lands at an exactly known row, which is what makes the surviving count exact.
    /// </summary>
    [Fact]
    public async Task UpsertMany_FailingPartway_KeepsTheSegmentsAlreadyCommitted()
    {
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        var committedSegments = BoundedBatchWriter.TransactionRows * 2;
        var batch = Enumerable.Range(0, committedSegments + (ObjectStateRepository.UpsertChunkRows * 3))
            .Select(i => State(i, $"hash{i}"))
            .ToList();

        // Well inside the THIRD segment, so two segments have certainly committed before it runs.
        batch[committedSegments + 50].LastHash = null!;

        var failure = await Assert.ThrowsAsync<SqliteException>(() => states.UpsertManyAsync(batch));
        Assert.Contains("NOT NULL", failure.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly the committed segments survive: the transaction in flight rolled back whole, and
        // nothing beyond it ever ran.
        Assert.Equal(committedSegments, await states.CountForJobAsync(_job.Id));

        var loaded = (await states.GetForJobDatabaseAsync(_job.Id, "Db1"))
            .Select(s => s.ObjectName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains($"usp_{committedSegments - 1:D6}", loaded); // last row of the last commit
        Assert.DoesNotContain($"usp_{committedSegments:D6}", loaded); // first row of the lost segment
    }

    /// <summary>
    /// The point of the whole change: another WRITER can take the database's write lock while a
    /// VLDB-scale batch is still running, instead of waiting out the entire batch and failing with
    /// SQLITE_BUSY once the busy timeout expires.
    ///
    /// Both halves are asserted against the batch actually being in flight. The competing writer is
    /// only allowed to try AFTER a reader has observed a partially committed batch — which is itself
    /// impossible under a single batch-wide transaction (the count would go straight from 0 to the
    /// total), and which rules out the writer having simply won the lock before the batch began.
    ///
    /// The competing write is a single blocking statement on the production busy timeout, so the
    /// assertion is an ordering one: it has to return BEFORE the batch does. Measured on this
    /// machine it waits 3-12 ms and lands 10+ seconds ahead of the batch's own completion — and with
    /// BoundedBatchWriter's boundary pause removed it instead landed 29-130 ms AFTER the batch, i.e.
    /// it had queued behind the whole thing. That is the margin this test is protecting.
    /// </summary>
    [Fact]
    public async Task AWriteFromAnotherConnection_LandsWhileALargeBatchIsStillRunning()
    {
        var factory = _provider.GetRequiredService<IDbConnectionFactory>();
        var states = _provider.GetRequiredService<IObjectStateRepository>();
        // Four segments. The writer is not released until the first has committed, so three
        // boundaries remain for it to find — measured, it takes the first one.
        var total = BoundedBatchWriter.TransactionRows * 4;
        var batchRows = Enumerable.Range(0, total).Select(i => State(i, $"hash{i}")).ToList();

        // Both helper connections are opened BEFORE the batch starts, so nothing in the measurement
        // is paying connection setup while the lock is contended.
        await using var reader = await factory.OpenAsync();
        await using var writer = await factory.OpenAsync();
        await using (var pragma = writer.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync();
        }

        var batchStart = Stopwatch.GetTimestamp();
        var batch = Task.Run(async () =>
        {
            await states.UpsertManyAsync(batchRows);
            return Stopwatch.GetTimestamp();
        });

        var sawPartialBatch = false;
        long writeStart = 0;
        long writeFinished = 0;

        // The competitor runs on a DEDICATED thread doing SYNCHRONOUS SQLite calls, not on the
        // thread pool. xUnit runs test classes in parallel, and a pool busy with the rest of the
        // suite can delay an async continuation long past the moment the lock was actually handed
        // over — which would make this test measure the .NET scheduler instead of the write lock,
        // and fail intermittently for a reason that has nothing to do with the code under test.
        var competitor = new Thread(() =>
        {
            while (!batch.IsCompleted && !sawPartialBatch)
            {
                // WAL readers are never blocked by a writer, so this observes commits as they land.
                // The window is wide (first commit to last), so polling gently is enough and leaves
                // the CPU to the batch rather than spinning against it.
                var visible = Count(reader);
                sawPartialBatch = visible > 0 && visible < total;
                if (!sawPartialBatch)
                {
                    Thread.Sleep(1);
                }
            }

            if (!sawPartialBatch)
            {
                return;
            }

            // ONE blocking write, issued with segments still to go. It returns when SQLite hands it
            // the lock, so the timestamp is when the batch let go — the quantity the performance
            // report calls the cross-host lock window.
            writeStart = Stopwatch.GetTimestamp();
            using var command = writer.CreateCommand();
            command.CommandText = "UPDATE jobs SET updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", _job.Id.ToString());
            command.ExecuteNonQuery();
            writeFinished = Stopwatch.GetTimestamp();
        })
        {
            IsBackground = true,
        };

        competitor.Start();
        competitor.Join();
        var batchFinished = await batch;

        Assert.True(sawPartialBatch,
            "No partially committed batch was ever visible — the batch still commits only once, at the end.");
        Assert.Equal(total, await states.CountForJobAsync(_job.Id));
        Assert.True(
            writeFinished < batchFinished,
            "The competing write did not land until the batch had finished — it queued behind the whole " +
            "batch, which is the single-transaction behaviour this change exists to remove. It waited " +
            $"{Stopwatch.GetElapsedTime(writeStart, writeFinished).TotalMilliseconds:F0} ms for a batch that " +
            $"ran for {Stopwatch.GetElapsedTime(batchStart, batchFinished).TotalMilliseconds:F0} ms.");
    }

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM object_states;";
        return (long)command.ExecuteScalar()!;
    }

    private async Task<SyncRun> InsertRunAsync(IRunRepository runs)
    {
        var run = new SyncRun
        {
            RunKey = "20260909-120000", JobId = _job.Id, JobName = _job.Name, Status = RunStatus.Running,
            ServerName = "s", Databases = "Db1", StartedAt = DateTimeOffset.UtcNow,
        };
        await runs.InsertAsync(run);
        return run;
    }

    private TrackedObjectState State(int i, string hash) => new()
    {
        JobId = _job.Id,
        DatabaseName = "Db1",
        ObjectType = SqlObjectType.StoredProcedure,
        SchemaName = "dbo",
        ObjectName = $"usp_{i:D6}",
        FilePath = $"procedures/dbo.usp_{i:D6}.sql",
        LastHash = hash,
        LastScriptedAt = DateTimeOffset.UtcNow,
        LastStatus = RunStatus.Succeeded,
    };

    public void Dispose()
    {
        _provider?.Dispose();
        TestDatabase.ReleasePool(_dbPath);
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp database file.
        }
    }
}
