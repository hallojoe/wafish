namespace SampleLocalService;

/// <summary>Bounded synthetic stream work. Counters support local cancellation verification.</summary>
public sealed class SampleStreamActivity
{
    private readonly object _gate = new();
    private int _active;
    private long _started, _completed, _cancelled, _events;
    public bool TryStart()
    {
        lock (_gate)
        {
            if (_active >= 16) return false;
            _active++; _started++; return true;
        }
    }
    public void Emitted() { lock (_gate) { _events++; } }
    public void Finish(bool completed) { lock (_gate) { _active--; if (completed) _completed++; else _cancelled++; } }
    public StreamActivitySnapshot Snapshot() { lock (_gate) { return new(_active, _started, _completed, _cancelled, _events); } }
}
public sealed record StreamActivitySnapshot(int Active, long Started, long Completed, long Cancelled, long Events);
