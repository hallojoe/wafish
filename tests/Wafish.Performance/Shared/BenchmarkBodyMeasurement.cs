using System.Buffers;
using System.Diagnostics;

namespace Wafish.Performance;

internal sealed record BenchmarkBodyMeasurement(long Bytes, long Reads, double FirstByteMs, double MaximumReadGapMs)
{
    public static async Task<BenchmarkBodyMeasurement> ReadAsync(Stream stream, long requestStart, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        long bytes = 0, reads = 0, previous = 0;
        double first = 0, maximumGap = 0;
        try
        {
            while (true)
            {
                var length = await stream.ReadAsync(buffer.AsMemory(0, 8192), cancellationToken);
                if (length == 0) break;
                var now = Stopwatch.GetTimestamp();
                if (reads == 0) first = Stopwatch.GetElapsedTime(requestStart, now).TotalMilliseconds;
                else maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now; reads++; bytes += length;
            }
            return new(bytes, reads, first, maximumGap);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
