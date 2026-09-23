using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Obsync.Engine;

namespace Obsync.Engine.Tests;

public sealed class ChannelPipelineTests
{
    private static async IAsyncEnumerable<int> Range(int count, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }

    [Fact]
    public async Task RunAsync_ProcessesEveryItemExactlyOnce()
    {
        var processed = new ConcurrentBag<int>();

        await ChannelPipeline.RunAsync(
            Range(1000),
            (i, _) =>
            {
                processed.Add(i);
                return Task.CompletedTask;
            },
            degreeOfParallelism: 4,
            CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 1000), processed.OrderBy(x => x));
    }

    [Fact]
    public async Task RunAsync_BoundsConcurrencyToDegreeOfParallelism()
    {
        var current = 0;
        var observedMax = 0;
        var gate = new object();

        await ChannelPipeline.RunAsync(
            Range(200),
            async (_, ct) =>
            {
                var now = Interlocked.Increment(ref current);
                lock (gate)
                {
                    observedMax = Math.Max(observedMax, now);
                }

                await Task.Delay(5, ct);
                Interlocked.Decrement(ref current);
            },
            degreeOfParallelism: 4,
            CancellationToken.None);

        Assert.True(observedMax <= 4, $"concurrency {observedMax} exceeded the degree of parallelism");
        Assert.True(observedMax >= 2, $"expected genuine parallelism but saw {observedMax}");
    }

    [Fact]
    public async Task RunAsync_ProducerFault_PropagatesWithoutHanging()
    {
        static async IAsyncEnumerable<int> Faulty([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
            throw new InvalidOperationException("producer boom");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChannelPipeline.RunAsync(Faulty(), (_, _) => Task.CompletedTask, 4, CancellationToken.None));

        Assert.Equal("producer boom", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ProducerFaultingBeforeItYields_StillReportsTheRealError()
    {
        // The regression this pins: consumers used to be started with Task.Run(body, token) against
        // the pipeline's own linked token. A producer that faults SYNCHRONOUSLY — before its first
        // await, which is exactly what SmoConnection.BuildServer and the metadata provider's
        // connection-string build do — cancels that token while RunAsync is still spawning
        // consumers, so the not-yet-started ones went straight to Canceled without running.
        // Task.WhenAll then threw TaskCanceledException ahead of the captured failure, the engine's
        // containment filters let OperationCanceledException through, and a genuine provider error
        // was recorded as "Run cancelled": no error text, no alert, all scripted work discarded.
        //
        // RunAsync_ProducerFault_PropagatesWithoutHanging cannot catch this — its producer yields
        // an item and awaits first, so every consumer is running by the time the fault lands.
        //
        // Deliberately many workers and repeated attempts: the old code masked the error ~9% of the
        // time on an idle pool, so a single attempt at low width would pass even when broken.
        static async IAsyncEnumerable<int> FaultsImmediately([EnumeratorCancellation] CancellationToken ct = default)
        {
            throw new InvalidOperationException("provider boom");
#pragma warning disable CS0162 // unreachable: required to make this an iterator
            yield break;
#pragma warning restore CS0162
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ChannelPipeline.RunAsync(
                    FaultsImmediately(), (_, _) => Task.CompletedTask, degreeOfParallelism: 32, CancellationToken.None));

            Assert.Equal("provider boom", ex.Message);
        }
    }

    [Fact]
    public async Task RunAsync_ConsumerFault_PropagatesWithoutDeadlock()
    {
        // A large source against a small worker pool keeps the bounded channel full, so a faulting
        // consumer must unblock the parked producer or this test would hang.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChannelPipeline.RunAsync(
                Range(10_000),
                (i, _) => i == 50 ? throw new InvalidOperationException("bad item") : Task.CompletedTask,
                degreeOfParallelism: 2,
                CancellationToken.None));

        Assert.Equal("bad item", ex.Message);
    }

    [Fact]
    public async Task RunAsync_Cancellation_SurfacesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChannelPipeline.RunAsync(
                Range(1_000_000),
                async (_, ct) =>
                {
                    await cts.CancelAsync();
                    await Task.Delay(50, ct);
                },
                degreeOfParallelism: 2,
                cts.Token));
    }
}
