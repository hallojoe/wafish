using System.Collections.Concurrent;
using WafIsh.Plugin.CircuitBreaker;
using WafIsh.Models;
using WafIsh.Storage;
using WafIsh.Infrastructure.State;

namespace WafIsh.Dashboard.Infrastructure.State;

/// <summary>
/// In-memory singleton implementation of <see cref="ICircuitStateStore"/>.
/// Replaces the static ConcurrentDictionary that was previously in CircuitBreakerMiddleware.
/// </summary>
public sealed class InMemoryCircuitStateStore : ICircuitStateStore
{
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();
    private readonly TimeProvider _time;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CircuitState, Timing> _timing = new();
    public InMemoryCircuitStateStore() : this(TimeProvider.System) { }
    public InMemoryCircuitStateStore(TimeProvider timeProvider) => _time = timeProvider;
    private sealed class Timing
    {
        public DateTime OpenedAt, AccessedAt;
        public long Opened, Accessed;
        public Queue<long> Samples { get; } = new();
    }
    private TimeSpan OpenElapsed(CircuitState state)
    {
        var timing = _timing.GetOrCreateValue(state);
        return timing.OpenedAt == state.OpenedAt && timing.OpenedAt != default
            ? _time.GetElapsedTime(timing.Opened) : _time.GetUtcNow().UtcDateTime - state.OpenedAt.ToUniversalTime();
    }
    private void Touch(CircuitState state)
    {
        var timing = _timing.GetOrCreateValue(state);
        state.LastAccessedAt = timing.AccessedAt = _time.GetUtcNow().UtcDateTime;
        timing.Accessed = _time.GetTimestamp();
    }
    private void Open(CircuitState state)
    {
        var timing = _timing.GetOrCreateValue(state);
        state.OpenedAt = timing.OpenedAt = _time.GetUtcNow().UtcDateTime;
        timing.Opened = _time.GetTimestamp();
    }
    private readonly object _stateLock = new();

    public int Count => _circuits.Count;

    public CircuitState GetOrAdd(string key, Func<string, CircuitState> factory)
        => _circuits.GetOrAdd(key, factory);

    public bool TryGet(string key, out CircuitState? state)
        => _circuits.TryGetValue(key, out state);

    public bool ContainsKey(string key) => _circuits.ContainsKey(key);

    public IReadOnlyList<KeyValuePair<string, CircuitState>> GetAll()
        => _circuits.ToList();

    public bool TryRemove(string key) => _circuits.TryRemove(key, out _);

    public void RemoveForCluster(string clusterId, string? clusterUid)
    {
        var keysToRemove = _circuits
            .Where(kv =>
                string.Equals(kv.Value.ClusterKeySnapshot, clusterId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(clusterUid)
                    && string.Equals(kv.Value.ClusterUid, clusterUid, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove)
            _circuits.TryRemove(key, out _);
    }

    public void ResetAll()
    {
        foreach (var kv in _circuits)
        {
            lock (kv.Value.SyncRoot)
            {
                ResetClosed(kv.Value);
            }
        }
    }

    public void RenameClusterKey(string oldClusterId, string newClusterId)
    {
        foreach (var state in _circuits.Values)
        {
            if (!string.Equals(state.ClusterKeySnapshot, oldClusterId, StringComparison.OrdinalIgnoreCase))
                continue;
            lock (_stateLock)
            {
                state.ClusterKeySnapshot = newClusterId;
            }
        }
    }

    public void EnsureCircuitExists(string clusterId, CircuitBreakerConfig cbConfig, string? clusterUid)
    {
        var circuitKey = BuildCircuitKey(clusterUid, clusterId, null);
        var state = _circuits.GetOrAdd(circuitKey, _ => new CircuitState(cbConfig));

        lock (_stateLock)
        {
            state.ApplyConfig(cbConfig);
            state.ClusterUid = clusterUid ?? StableUid.FromKey("cluster", clusterId);
            state.ClusterKeySnapshot = clusterId;
            state.DestinationUid = "any";
            state.DestinationKeySnapshot = "any";
        }
    }

    public bool IsCircuitOpen(string clusterId, string? destinationId = null, string? clusterUid = null)
    {
        var key = BuildCircuitKey(clusterUid, clusterId, destinationId);
        if (!_circuits.TryGetValue(key, out var state)) return false;
        lock (state.SyncRoot)
        {
            return state.Status == CircuitStatus.Open && OpenElapsed(state) < state.RecoveryTimeout;
        }
    }

    public bool TryAcquire(
        string clusterId,
        string destinationId,
        string? clusterUid = null,
        CircuitBreakerConfig? config = null)
    {
        var key = BuildCircuitKey(clusterUid, clusterId, destinationId);
        var state = _circuits.GetOrAdd(key, _ => new CircuitState());
        lock (state.SyncRoot)
        {
            if (config != null) state.ApplyConfig(config);
            state.ClusterUid = clusterUid ?? StableUid.FromKey("cluster", clusterId);
            state.ClusterKeySnapshot = clusterId;
            state.DestinationUid = ResolveDestinationUid(destinationId);
            state.DestinationKeySnapshot = destinationId;
            Touch(state);
            if (state.Status == CircuitStatus.Open)
            {
                if (OpenElapsed(state) < state.RecoveryTimeout) return false;
                state.Generation++;
                state.Status = CircuitStatus.HalfOpen;
                state.HalfOpenRequests = 0;
            }

            if (state.Status == CircuitStatus.HalfOpen)
            {
                if (state.HalfOpenRequests >= state.MaxHalfOpenAttempts) return false;
                state.HalfOpenRequests++;
            }

            return true;
        }
    }

    public CircuitAttempt? TryAcquireAttempt(string clusterId, string destinationId, CircuitBreakerConfig config)
    {
        var key = BuildCircuitKey(null, clusterId, destinationId);
        lock (_stateLock)
        {
            if (!_circuits.ContainsKey(key) && _circuits.Count >= 10_000) return null;
            var state = _circuits.GetOrAdd(key, _ => new CircuitState());
            lock (state.SyncRoot)
            {
                if (!TryAcquire(clusterId, destinationId, config: config)) return null;
                state.ActiveAttempts++;
                var generation = state.Generation;
                var probe = state.Status == CircuitStatus.HalfOpen;
                return new CircuitAttempt(failed =>
                {
                    lock (state.SyncRoot)
                    {
                        state.ActiveAttempts--;
                        if (generation != state.Generation || !_circuits.TryGetValue(key, out var current) || !ReferenceEquals(current, state)) return;
                        if (failed.HasValue) RecordOutcome(clusterId, destinationId, failed.Value, config: config);
                        else if (probe) state.HalfOpenRequests = Math.Max(0, state.HalfOpenRequests - 1);
                    }
                });
            }
        }
    }

    public void RecordOutcome(
        string clusterId,
        string destinationId,
        bool failed,
        string? clusterUid = null,
        CircuitBreakerConfig? config = null)
    {
        var key = BuildCircuitKey(clusterUid, clusterId, destinationId);
        var state = _circuits.GetOrAdd(key, _ => new CircuitState());
        lock (state.SyncRoot)
        {
            if (config != null) state.ApplyConfig(config);
            var now = _time.GetUtcNow().UtcDateTime;
            Touch(state);
            if (state.Status == CircuitStatus.HalfOpen)
            {
                if (failed)
                {
                    state.Generation++;
                    state.Status = CircuitStatus.Open;
                    Open(state);
                    state.HalfOpenRequests = 0;
                }
                else
                {
                    ResetClosed(state);
                }
                return;
            }

            if (state.Status != CircuitStatus.Closed) return;
            state.Samples.Enqueue((now, failed));
            if (failed) state.SampleFailures++;
            var samples = _timing.GetOrCreateValue(state).Samples;
            samples.Enqueue(_time.GetTimestamp());
            while (state.Samples.TryPeek(out var sample) && samples.TryPeek(out var timestamp) && _time.GetElapsedTime(timestamp) > state.SamplingDuration)
            {
                state.Samples.Dequeue();
                samples.Dequeue();
                if (sample.Failed) state.SampleFailures--;
            }
            state.ConsecutiveFailures = state.SampleFailures;
            if (state.Samples.Count >= state.MinimumThroughput &&
                (double)state.SampleFailures / state.Samples.Count >= state.FailureRatio)
            {
                state.Generation++;
                state.Status = CircuitStatus.Open;
                Open(state);
            }
        }
    }

    private void ResetClosed(CircuitState state)
    {
        state.Generation++;
        state.Status = CircuitStatus.Closed;
        state.ConsecutiveFailures = 0;
        state.HalfOpenRequests = 0;
        state.SampleFailures = 0;
        state.Samples.Clear();
        _timing.GetOrCreateValue(state).Samples.Clear();
    }

    public IReadOnlyList<CircuitStateInfo> GetAllStateInfos()
    {
        return _circuits.Select(kv => new CircuitStateInfo
        {
            Key = kv.Key,
            ClusterUid = kv.Value.ClusterUid,
            ClusterKeySnapshot = kv.Value.ClusterKeySnapshot,
            DestinationUid = kv.Value.DestinationUid,
            DestinationKeySnapshot = kv.Value.DestinationKeySnapshot,
            Status = kv.Value.Status.ToString(),
            ConsecutiveFailures = kv.Value.ConsecutiveFailures,
            FailureThreshold = kv.Value.FailureThreshold,
            RecoveryTimeout = kv.Value.RecoveryTimeout,
            RecoveryTimeoutSeconds = (int)kv.Value.RecoveryTimeout.TotalSeconds,
            HalfOpenRequests = kv.Value.HalfOpenRequests,
            MaxHalfOpenAttempts = kv.Value.MaxHalfOpenAttempts,
            OpenedAt = kv.Value.OpenedAt == DateTime.MinValue ? null : kv.Value.OpenedAt,
            LastAccessedAt = kv.Value.LastAccessedAt
        }).ToList();
    }

    public void CleanupStale(TimeSpan threshold)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_stateLock)
            foreach (var pair in _circuits)
                lock (pair.Value.SyncRoot)
                    if (pair.Value.ActiveAttempts == 0 && pair.Value.Status == CircuitStatus.Closed && (_timing.TryGetValue(pair.Value, out var timing) && timing.AccessedAt == pair.Value.LastAccessedAt
                        ? _time.GetElapsedTime(timing.Accessed) : now - pair.Value.LastAccessedAt.ToUniversalTime()) > threshold)
                        _circuits.TryRemove(pair.Key, out _);
    }

    /// <summary>Acquires the internal lock for state mutations. Use sparingly.</summary>
    internal void Lock(Action<CircuitState> action, CircuitState state)
    {
        lock (_stateLock)
        {
            action(state);
        }
    }

    // ---- Static helpers (pure functions, safe to keep static) ----

    internal static string BuildCircuitKey(string? clusterUid, string clusterKey, string? destinationKey)
    {
        var resolvedClusterUid = string.IsNullOrWhiteSpace(clusterUid)
            ? StableUid.FromKey("cluster", clusterKey) : clusterUid;
        return $"{resolvedClusterUid}:{ResolveDestinationUid(destinationKey)}";
    }

    internal static string ResolveDestinationUid(string? destinationKey)
        => string.IsNullOrWhiteSpace(destinationKey) ? "any" : StableUid.FromKey("destination", destinationKey);

    internal static (string ClusterId, string? DestinationId) ParseCircuitKey(string key)
    {
        var lastColon = key.LastIndexOf(':');
        if (lastColon < 0) return (key, null);
        var cluster = key[..lastColon];
        var dest = key[(lastColon + 1)..];
        return dest == "any" ? (cluster, null) : (cluster, dest);
    }
}
