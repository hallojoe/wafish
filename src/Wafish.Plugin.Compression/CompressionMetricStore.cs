using System.Diagnostics.Metrics;
using Wafish.Services;

namespace Wafish.Plugin.Compression;

public enum CompressionMetricOutcome { Completed, Cancelled, Faulted }
public readonly record struct CompressionMetricKey(string RouteId, string Encoding, CompressionMetricOutcome Outcome);
public sealed record CompressionMetricSnapshot(long Responses, long InputBytes, long OutputBytes, long CodecDurationTicks);

/// <summary>Bounded, process-local compression measurements. No request values become metric dimensions.</summary>
public sealed class CompressionMetricStore : IDisposable
{
    public const int MaximumSeries = 1024;
    private readonly Meter _meter = new("Wafish.Plugin.Compression", "1.0");
    private readonly object _gate = new();
    private readonly Dictionary<CompressionMetricKey, CompressionMetricSnapshot> _series = new();
    private readonly IGatewaySnapshotPublisher? _snapshots;
    private HashSet<string>? _routes;
    private long _version = -1;
    private bool _disposed;

    public CompressionMetricStore() : this(null) { }
    public CompressionMetricStore(IGatewaySnapshotPublisher? snapshots)
    {
        _snapshots = snapshots;
        _meter.CreateObservableCounter("wafish.compression.responses", () => Observe(s => s.Responses), "{response}");
        _meter.CreateObservableCounter("wafish.compression.input_bytes", () => Observe(s => s.InputBytes), "By");
        _meter.CreateObservableCounter("wafish.compression.output_bytes", () => Observe(s => s.OutputBytes), "By");
        _meter.CreateObservableCounter("wafish.compression.codec_duration", () => GetSnapshots().Select(p =>
            new Measurement<double>(p.Value.CodecDurationTicks / (double)TimeSpan.TicksPerSecond, Tags(p.Key))), "s",
            "Elapsed codec operation time excluding destination writes and flushes; not CPU time.");
    }
    private IEnumerable<Measurement<long>> Observe(Func<CompressionMetricSnapshot, long> value) => GetSnapshots()
        .Select(p => new Measurement<long>(value(p.Value), Tags(p.Key)));
    private static KeyValuePair<string, object?>[] Tags(CompressionMetricKey key) =>
        [new("wafish.route.id", key.RouteId), new("compression.encoding", key.Encoding),
         new("compression.outcome", key.Outcome switch { CompressionMetricOutcome.Cancelled => "cancelled", CompressionMetricOutcome.Faulted => "faulted", _ => "completed" })];

    public void Record(string routeId, string encoding, CompressionMetricOutcome outcome, long inputBytes, long outputBytes, TimeSpan codecDuration)
    {
        if (string.IsNullOrWhiteSpace(routeId) || !Enum.IsDefined(outcome)) return;
        var key = new CompressionMetricKey(routeId, encoding is "br" or "gzip" ? encoding : "identity", outcome);
        lock (_gate)
        {
            if (_disposed) return;
            Refresh();
            if (_routes != null && !_routes.Contains(routeId)) return;
            if (!_series.TryGetValue(key, out var previous))
            {
                if (_series.Count >= MaximumSeries) return;
                previous = new(0, 0, 0, 0);
            }
            _series[key] = new(previous.Responses + 1, previous.InputBytes + Math.Max(0, inputBytes),
                previous.OutputBytes + Math.Max(0, outputBytes), previous.CodecDurationTicks + Math.Max(0, codecDuration.Ticks));
        }
    }
    public IReadOnlyDictionary<CompressionMetricKey, CompressionMetricSnapshot> GetSnapshots()
    {
        lock (_gate) { Refresh(); return new Dictionary<CompressionMetricKey, CompressionMetricSnapshot>(_series); }
    }
    private void Refresh()
    {
        if (_snapshots is null || _snapshots.Current.Version == _version) return;
        var snapshot = _snapshots.Current;
        _routes = snapshot.Routes.Select(r => r.RouteId).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _series.Keys.Where(k => !_routes.Contains(k.RouteId)).ToArray()) _series.Remove(key);
        _version = snapshot.Version;
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _series.Clear(); _meter.Dispose(); }
    }
}
