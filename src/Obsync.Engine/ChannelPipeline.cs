using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Obsync.Engine;

/// <summary>
/// A bounded producer/consumer pipeline: a single producer pumps an <see cref="IAsyncEnumerable{T}"/>
/// into a bounded channel (providing backpressure so memory stays flat for very large sources),
/// and a fixed pool of consumers processes items concurrently. The first real failure — in the
/// producer or any consumer — tears the whole pipeline down (no deadlock on a full channel) and is
/// rethrown faithfully; a genuine cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
internal static class ChannelPipeline
{
    public static async Task RunAsync<T>(
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> process,
        int degreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var workers = Math.Max(1, degreeOfParallelism);

        // Capacity scales with the worker count: a little look-ahead, but bounded so a slow
        // consumer pool throttles the producer (and therefore the SQL reader) rather than buffering.
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(workers * 2)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        // The first non-cancellation fault wins; sibling OperationCanceledExceptions caused by our
        // own teardown are ignored so a real error is never masked as a cancellation.
        var gate = new object();
        Exception? failure = null;
        void Record(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return;
            }

            lock (gate)
            {
                failure ??= ex;
            }
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(item, token).ConfigureAwait(false);
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                Record(ex);
                channel.Writer.Complete(ex); // makes draining consumers observe the failure and stop
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
        }, token);

        var consumers = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            consumers[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        await process(item, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Record(ex);
                    // Unblock the producer (which may be parked on a full channel) and peers.
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                }
            }, token);
        }

        // Consumers and the producer swallow their own faults into `failure`, so these awaits do not
        // throw; we surface the captured outcome deterministically afterwards.
        await Task.WhenAll(consumers).ConfigureAwait(false);
        await producer.ConfigureAwait(false);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
