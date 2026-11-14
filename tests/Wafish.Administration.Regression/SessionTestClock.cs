internal sealed class SessionTestClock : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _utc = DateTimeOffset.UtcNow;
    private readonly List<SessionTimer> _timers = [];
    private readonly object _gate = new();
    public int TimerCount { get { lock (_gate) { return _timers.Count; } } }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate)
        {
            return _ticks;
        }
    }
    public override DateTimeOffset GetUtcNow() { lock (_gate)
        {
            return _utc;
        }
    }
    public void ShiftUtc(TimeSpan duration) { lock (_gate)
        {
            _utc += duration;
        }
    }
    public void Advance(TimeSpan duration)
    {
        SessionTimer[] timers;
        lock (_gate) { _ticks += duration.Ticks; _utc += duration; timers = _timers.ToArray(); }
        foreach (var timer in timers)
        {
            timer.Fire();
        }
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate) { var timer = new SessionTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
    }
    private sealed class SessionTimer(SessionTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due = long.MaxValue, _period;
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock._gate) { _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; _period = period.Ticks; return true; } }
        public void Fire() { lock (clock._gate) { if (_due > clock._ticks) { return; } _due = _period > 0 ? clock._ticks + _period : long.MaxValue; } callback(state); }
        public void Dispose() { lock (clock._gate) { _due = long.MaxValue; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
