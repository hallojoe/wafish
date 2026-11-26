using System.Diagnostics;
using System.Threading.Channels;

namespace Wafish.Performance;

internal sealed record ArrivalStatistics(long Offered, long Started, long Dropped, long CancelledBeforeStart,
    double OfferedPerSecond, double QueueP50Ms, double QueueP95Ms, double QueueP99Ms);

internal static class BenchmarkArrivalSchedule
{
    public static async Task<ArrivalStatistics> RunAsync(int concurrency, TimeSpan duration, int rate, int capacity,
        Func<long, CancellationToken, Task> request, CancellationToken cancellationToken = default)
    {
        if (concurrency is < 1 or > 4096 || duration < TimeSpan.Zero || duration > TimeSpan.FromHours(1) ||
            rate is < 0 or > 100000 || capacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(rate), "Invalid arrival schedule bounds.");
        var start = Stopwatch.GetTimestamp();
        var stop = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration + TimeSpan.FromSeconds(12));
        var queueDelay = new BoundedLatencyHistogram();
        long offered = 0, started = 0, dropped = 0, cancelled = 0;
        if (rate == 0)
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (Stopwatch.GetTimestamp() < stop && !deadline.IsCancellationRequested)
                {
                    var due = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref offered); Interlocked.Increment(ref started);
                    await request(due, deadline.Token);
                }
            }));
        }
        else
        {
            var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
            { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            var workers = Enumerable.Range(0, concurrency).Select(async _ =>
            {
                await foreach (var due in channel.Reader.ReadAllAsync())
                {
                    if (deadline.IsCancellationRequested) { Interlocked.Increment(ref cancelled); continue; }
                    Interlocked.Increment(ref started);
                    queueDelay.Record(Stopwatch.GetElapsedTime(due).TotalMilliseconds);
                    await request(due, deadline.Token);
                }
            }).ToArray();
            var count = checked((long)Math.Ceiling(duration.TotalSeconds * rate));
            try
            {
                for (long index = 0; index < count; index++)
                {
                    var due = start + (long)(index * (double)Stopwatch.Frequency / rate);
                    while (Stopwatch.GetTimestamp() < due)
                    {
                        var remaining = TimeSpan.FromSeconds((due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, deadline.Token);
                    }
                    offered++;
                    if (Stopwatch.GetTimestamp() >= stop || !channel.Writer.TryWrite(due)) dropped++;
                    if ((index & 255) == 255) await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            finally { channel.Writer.TryComplete(); }
            await Task.WhenAll(workers);
        }
        return new(offered, started, dropped, cancelled, duration.TotalSeconds == 0 ? 0 : offered / duration.TotalSeconds,
            queueDelay.Percentile(.5), queueDelay.Percentile(.95), queueDelay.Percentile(.99));
    }
}
