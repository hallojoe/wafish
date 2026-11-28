using System.Threading.RateLimiting;
using Wafish.Infrastructure.State;

namespace Wafish.Dashboard.Infrastructure.State;

/// <summary>Bounded admission with ownership held through queued acquisition and downstream work.</summary>
public sealed class InMemoryRateLimiterStore : IRateLimiterStore, IGroupedRateLimiterStore, IWeightedRateLimiterStore, IWeightedGroupedRateLimiterStore, IDisposable
{
    private readonly TimeProvider _time;
    public InMemoryRateLimiterStore() : this(TimeProvider.System) { }
    public InMemoryRateLimiterStore(TimeProvider timeProvider) => _time = timeProvider;
    public const int MaximumPartitions = 2000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _limiters = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Group, string Partition), Entry> _groupPartitions = new();
    private readonly Dictionary<string, GroupOwner> _groups = new(StringComparer.Ordinal);
    private bool _disposed;
    public int Count { get { lock (_gate) { return _limiters.Count + _groupPartitions.Count; } } }

    private Entry? GetEntry(string key, Func<RateLimiter> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_limiters.TryGetValue(key, out var entry))
        {
            return entry;
        }

        if (_limiters.Count + _groupPartitions.Count >= MaximumPartitions)
        {
            return null;
        }

        entry = new Entry(new RateLimiterEntry(factory()));
        _limiters.Add(key, entry);
        return entry;
    }

    public ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default)
        => AcquireWeightedAsync(key, 1, factory, cancellationToken);

    public async ValueTask<RateLimitLease?> AcquireWeightedAsync(string key, int permitCount, Func<RateLimiter> factory, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
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
            var lease = await entry.Value.Limiter.AcquireAsync(permitCount, cancellationToken);
            return new OwnedLease(lease, () => Release(entry));
        }
        catch { Release(entry); throw; }
    }

    public bool IsGroupPolicyCompatible(string group, string policy)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return !_groups.TryGetValue(group, out var owner) || owner.Policy == policy;
        }
    }

    public ValueTask<RateLimitLease?> AcquireGroupedAsync(string group, string policy, string partition,
        Func<RateLimiter> factory, CancellationToken cancellationToken = default)
        => AcquireWeightedGroupedAsync(group, policy, partition, 1, factory, cancellationToken);

    public async ValueTask<RateLimitLease?> AcquireWeightedGroupedAsync(string group, string policy, string partition, int permitCount,
        Func<RateLimiter> factory, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        ArgumentNullException.ThrowIfNull(partition);
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_groups.TryGetValue(group, out var owner) && owner.Policy != policy)
                throw new RateLimitGroupPolicyConflictException(group);
            var key = (group, partition);
            if (!_groupPartitions.TryGetValue(key, out entry!))
            {
                if (_limiters.Count + _groupPartitions.Count >= MaximumPartitions) return null;
                // A failing factory must not pin a group or consume partition capacity.
                entry = new Entry(new RateLimiterEntry(factory()));
                owner ??= new GroupOwner(policy);
                _groups[group] = owner;
                owner.Partitions++;
                _groupPartitions.Add(key, entry);
            }
            entry.Users++;
            entry.Value.LastAccessedAt = _time.GetUtcNow().UtcDateTime;
            entry.LastAccess = _time.GetTimestamp();
        }
        try
        {
            var lease = await entry.Value.Limiter.AcquireAsync(permitCount, cancellationToken);
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
            foreach (var pair in _groupPartitions.ToArray())
            {
                var entry = pair.Value;
                if (entry.Users != 0 || _time.GetElapsedTime(entry.LastAccess) <= staleThreshold ||
                    entry.Value.Limiter.IdleDuration is not { } idle || idle <= staleThreshold) continue;
                _groupPartitions.Remove(pair.Key);
                var owner = _groups[pair.Key.Group];
                if (--owner.Partitions == 0) _groups.Remove(pair.Key.Group);
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
            foreach (var entry in _limiters.Values.Concat(_groupPartitions.Values).Where(e => e.Users == 0))
            {
                entry.Value.Limiter.Dispose();
            }

            _limiters.Clear();
            _groupPartitions.Clear();
            _groups.Clear();
        }
    }

    private sealed class Entry(RateLimiterEntry value)
    {
        public RateLimiterEntry Value { get; } = value;
        public int Users { get; set; }
        public long LastAccess { get; set; }
    }
    private sealed class GroupOwner(string policy)
    {
        public string Policy { get; } = policy;
        public int Partitions { get; set; }
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
