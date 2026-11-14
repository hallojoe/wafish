using Microsoft.Extensions.Options;

namespace Wafish.Plugin.Retry;

public sealed class ClusterRetryBudgetOptions
{
    public int RetriesPerWindow { get; set; } = 100;
    public int WindowSeconds { get; set; } = 1;
}

/// <summary>Host-local fixed-window allowance shared by routes targeting the same cluster. Initial attempts are never charged.</summary>
public sealed class ClusterRetryBudget
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _limit;
    private readonly TimeSpan _duration;
    private readonly TimeProvider _clock;
    private long _denied;
    private long _lastSweep;
    public long DeniedRetries => Interlocked.Read(ref _denied);
    public int RetainedClusters { get { lock (_gate) return _windows.Count; } }

    public ClusterRetryBudget(IOptions<ClusterRetryBudgetOptions> options, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Value.RetriesPerWindow);
        if (options.Value.WindowSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(options));
        _limit = options.Value.RetriesPerWindow;
        _duration = TimeSpan.FromSeconds(options.Value.WindowSeconds);
        _clock = clock ?? TimeProvider.System;
        _lastSweep = _clock.GetTimestamp();
    }

    public bool TryAcquire(string? clusterId)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            // Retire expired identities before admitting new clusters. No timers or per-request key growth.
            if (clusterId is not null && !_windows.ContainsKey(clusterId) && _clock.GetElapsedTime(_lastSweep, now) >= _duration)
            {
                foreach (var key in _windows.Where(p => _clock.GetElapsedTime(p.Value.Start, now) >= _duration).Select(p => p.Key).ToArray())
                    _windows.Remove(key);
                _lastSweep = now;
            }
            if (clusterId is null || _limit == 0 || (!_windows.ContainsKey(clusterId) && _windows.Count >= 10_000))
            {
                Interlocked.Increment(ref _denied);
                return false;
            }
            if (!_windows.TryGetValue(clusterId, out var window) || _clock.GetElapsedTime(window.Start, now) >= _duration)
                window = new(now, 0);
            if (window.Used >= _limit)
            {
                Interlocked.Increment(ref _denied);
                return false;
            }
            _windows[clusterId] = window with { Used = window.Used + 1 };
            return true;
        }
    }
    private sealed record Window(long Start, int Used);
}
