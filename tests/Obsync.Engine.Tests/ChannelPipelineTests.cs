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
