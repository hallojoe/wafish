using System.Threading.RateLimiting;
using WafIsh.Infrastructure.State;

namespace WafIsh.Dashboard.Infrastructure.State;

/// <summary>Bounded admission with ownership held through queued acquisition and downstream work.</summary>
public sealed class InMemoryRateLimiterStore : IRateLimiterStore, IDisposable
{
    private readonly TimeProvider _time;
    public InMemoryRateLimiterStore() : this(TimeProvider.System) { }
    public InMemoryRateLimiterStore(TimeProvider timeProvider) => _time = timeProvider;
    public const int MaximumPartitions = 2000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _limiters = new(StringComparer.Ordinal);
    private bool _disposed;
    public int Count { get { lock (_gate) { return _limiters.Count; } } }

    private Entry? GetEntry(string key, Func<RateLimiter> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_limiters.TryGetValue(key, out var entry))
        {
            return entry;
        }

        if (_limiters.Count >= MaximumPartitions)
        {
            return null;
        }

        entry = new Entry(new RateLimiterEntry(factory()));
        _limiters.Add(key, entry);
        return entry;
    }

    public async ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = GetEntry(key, factory);
            if (entry == null)
            {
                return null;
            }

            entry.Users++;
            entry.Value.LastAccessedAt = _time.GetUtcNow().UtcDateTime;
            entry.LastAccess = _time.GetTimestamp();
        }
        try
        {
            var lease = await entry.Value.Limiter.AcquireAsync(1, cancellationToken);
            return new OwnedLease(lease, () => Release(entry));
        }
        catch { Release(entry); throw; }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            entry.Value.LastAccessedAt = _time.GetUtcNow().UtcDateTime;
            entry.LastAccess = _time.GetTimestamp();
            if (_disposed && entry.Users == 0)
            {
                entry.Value.Limiter.Dispose();
            }
        }
    }

    public void Cleanup(TimeSpan staleThreshold, int maxCount)
    {
        lock (_gate)
        {
            foreach (var pair in _limiters.ToArray())
            {
                var entry = pair.Value;
                // Never reset an active quota to admit a new identity.
                if (entry.Users != 0 || _time.GetElapsedTime(entry.LastAccess) <= staleThreshold ||
                    entry.Value.Limiter.IdleDuration is not { } idle || idle <= staleThreshold)
                {
                    continue;
                }

                _limiters.Remove(pair.Key);
                entry.Value.Limiter.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _limiters.Values.Where(e => e.Users == 0))
            {
                entry.Value.Limiter.Dispose();
            }

            _limiters.Clear();
        }
    }

    private sealed class Entry(RateLimiterEntry value)
    {
        public RateLimiterEntry Value { get; } = value;
        public int Users { get; set; }
        public long LastAccess { get; set; }
    }
    private sealed class OwnedLease(RateLimitLease lease, Action release) : RateLimitLease
    {
        private int _disposed;
        public override bool IsAcquired => lease.IsAcquired;
        public override IEnumerable<string> MetadataNames => lease.MetadataNames;
        public override bool TryGetMetadata(string metadataName, out object? metadata) => lease.TryGetMetadata(metadataName, out metadata);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { lease.Dispose(); } finally { release(); }
            }
            base.Dispose(disposing);
        }
    }
}
