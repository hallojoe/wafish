namespace Wafish.Performance;

/// <summary>Fixed-memory upper-bound percentiles. Read percentiles after writers have completed.</summary>
internal sealed class BoundedLatencyHistogram
{
    public const double MinimumMilliseconds = 0.001;
    public const double MaximumMilliseconds = 86_400_000;
    public const double RelativeError = 0.01;
    private static readonly double LogRatio = Math.Log(1 + RelativeError);
    private readonly long[] _buckets = new long[(int)Math.Ceiling(Math.Log(MaximumMilliseconds / MinimumMilliseconds) / LogRatio) + 1];
    public int RetainedBytes => _buckets.Length * sizeof(long);
    public long Count => _buckets.Sum(value => value);

    public void Record(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > MaximumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Latency must be finite and between zero and 24 hours.");
        var index = milliseconds <= MinimumMilliseconds ? 0 : (int)Math.Ceiling(Math.Log(milliseconds / MinimumMilliseconds) / LogRatio);
        Interlocked.Increment(ref _buckets[Math.Clamp(index, 0, _buckets.Length - 1)]);
    }

    public double Percentile(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var total = Count;
        if (total == 0) return 0;
        var rank = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long cumulative = 0;
        for (var index = 0; index < _buckets.Length; index++)
        {
            cumulative += _buckets[index];
            if (cumulative >= rank) return Math.Min(MaximumMilliseconds, MinimumMilliseconds * Math.Pow(1 + RelativeError, index));
        }
        throw new InvalidOperationException("Latency writers must complete before percentile reads.");
    }
}
