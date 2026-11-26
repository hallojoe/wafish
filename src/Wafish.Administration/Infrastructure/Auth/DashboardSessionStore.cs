using System.Security.Cryptography;
using System.Text;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Bounded host-local sessions. Restart, sign-out, and security changes invalidate owned tokens.</summary>
public sealed class DashboardSessionStore(TimeProvider? clock = null) : IDisposable
{
    public const int Capacity = 10000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private long _generation;
    private bool _disposed;
    internal long Generation { get { lock (_gate) return _generation; } }

    internal bool TryCreate(string username, string secret, long generation, out string token)
    {
        lock (_gate)
        {
            token = string.Empty;
            if (_disposed || generation != _generation) return false;
            foreach (var expired in _sessions.Where(x => x.Value.Lifetime.IsCancellationRequested).Select(x => x.Key).ToArray())
            {
                _sessions.Remove(expired, out var session);
                session!.Lifetime.Dispose();
            }
            if (_sessions.Count >= Capacity) return false;
            token = DashboardJwtHelper.GenerateToken(username, secret, _clock);
            _sessions.Add(Key(token), new(new CancellationTokenSource(TimeSpan.FromHours(8), _clock)));
            return true;
        }
    }

    internal bool TryCommit(string token, long generation, Action commit)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || !_sessions.TryGetValue(Key(token), out var session) || session.Lifetime.IsCancellationRequested) return false;
            commit();
            return true;
        }
    }

    internal bool IsActive(string token)
    {
        lock (_gate) return !_disposed && _sessions.TryGetValue(Key(token), out var session) && !session.Lifetime.IsCancellationRequested;
    }

    internal CancellationToken Lifetime(string? token)
    {
        lock (_gate) return token != null && _sessions.TryGetValue(Key(token), out var session)
            ? session.Lifetime.Token : new CancellationToken(canceled: true);
    }

    internal void Revoke(string? token)
    {
        Session? session;
        lock (_gate)
        {
            if (token == null || !_sessions.Remove(Key(token), out session)) return;
        }
        session.Lifetime.Cancel();
        session.Lifetime.Dispose();
    }

    internal void InvalidateAll()
    {
        Session[] sessions;
        lock (_gate)
        {
            _generation++;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions) { session.Lifetime.Cancel(); session.Lifetime.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        InvalidateAll();
    }
    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record Session(CancellationTokenSource Lifetime);
}
