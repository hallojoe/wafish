using Wafish.Infrastructure.State;

namespace Wafish.Dashboard.Infrastructure.State;

public sealed class InMemoryDestinationMaintenanceStore(TimeProvider clock) : IDestinationMaintenanceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Cluster, string Destination), Entry> _entries = new();
    private sealed record Entry(DestinationMaintenanceWindow Window, long Started, TimeSpan Duration);
    public DestinationMaintenanceWindow Set(string clusterId, string destinationId, string actor, string reason, TimeSpan duration, bool allowRecoveryProbes)
    {
        Validate(clusterId, 256); Validate(destinationId, 256); Validate(actor, 256); Validate(reason, 1024);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromDays(1)) throw new ArgumentException("Maintenance duration must be 1–86400 seconds.");
        lock (_sync)
        {
            Expire();
            var key = (clusterId, destinationId);
            if (_entries.Count >= 10000 && !_entries.ContainsKey(key)) throw new InvalidOperationException("Maintenance capacity reached.");
            var now = clock.GetUtcNow();
            var window = new DestinationMaintenanceWindow(clusterId, destinationId, actor, reason, now, now + duration, allowRecoveryProbes);
            _entries[key] = new(window, clock.GetTimestamp(), duration);
            return window;
        }
    }
    public bool Remove(string clusterId, string destinationId) { lock (_sync) return _entries.Remove((clusterId, destinationId)); }
    public IReadOnlyList<DestinationMaintenanceWindow> GetAll()
    {
        lock (_sync) { Expire(); return _entries.Values.Select(e => e.Window).OrderBy(w => w.ClusterId, StringComparer.Ordinal).ThenBy(w => w.DestinationId, StringComparer.Ordinal).ToArray(); }
    }
    public bool AllowsTraffic(string clusterId, string destinationId, bool recoveryProbe = false)
    {
        lock (_sync)
        {
            var key = (clusterId, destinationId);
            if (!_entries.TryGetValue(key, out var entry)) return true;
            if (clock.GetElapsedTime(entry.Started) >= entry.Duration) { _entries.Remove(key); return true; }
            return recoveryProbe && entry.Window.AllowRecoveryProbes;
        }
    }
    private void Expire()
    {
        foreach (var pair in _entries.Where(e => clock.GetElapsedTime(e.Value.Started) >= e.Value.Duration).ToArray()) _entries.Remove(pair.Key);
    }
    private static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Maintenance fields must contain bounded printable text.");
    }
}
