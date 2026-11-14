using Microsoft.AspNetCore.Http;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Bounded, process-local credential attempt windows. Forwarded headers are never read here.</summary>
public sealed class AuthenticationAttemptLimiter(TimeProvider? timeProvider = null)
{
    public const int AttemptLimit = 20;
    public const int PartitionLimit = 2048;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <summary>Reserve an attempt before checking credentials. Successful direct credentials can refund it.</summary>
    public bool TryAcquire(HttpContext context, string purpose, out Action refund)
    {
        var address = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        var key = purpose + ":" + address;
        return TryReserve(context, key, AttemptLimit, out refund);
    }

    /// <summary>Limit the configured account across source addresses without storing caller-supplied account names.</summary>
    public bool TryAcquireAccount(HttpContext context)
        => TryReserve(context, "configured-login-account", 100, out _);

    private bool TryReserve(HttpContext context, string key, int limit, out Action refund)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            foreach (var expired in _counters.Where(x => _clock.GetElapsedTime(x.Value.Start, now) >= Window).Select(x => x.Key).ToArray())
            {
                _counters.Remove(expired);
            }

            if (!_counters.TryGetValue(key, out var counter))
            {
                if (_counters.Count >= PartitionLimit)
                {
                    Reject(context, 60);
                    refund = static () => { };
                    return false;
                }
                counter = new Counter(now);
                _counters.Add(key, counter);
            }
            if (counter.Count >= limit)
            {
                Reject(context, Math.Max(1, (int)Math.Ceiling((Window - _clock.GetElapsedTime(counter.Start, now)).TotalSeconds)));
                refund = static () => { };
                return false;
            }
            counter.Count++;
            var refunded = false;
            refund = () => { lock (_gate) { if (!refunded) { counter.Count--; refunded = true; } } };
            return true;
        }
    }

    private static void Reject(HttpContext context, int seconds)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private sealed class Counter(long start)
    {
        public long Start { get; } = start;
        public int Count { get; set; }
    }
}
