using Microsoft.Extensions.Options;
namespace WafIsh.Plugin.Cache;

public sealed class ResponseCacheBudgetOptions
{
    public long SizeLimitBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>One budget covers stored entries, pending captures, and entries still being served after eviction.</summary>
public sealed class ResponseCacheStore(IOptions<ResponseCacheBudgetOptions> options, TimeProvider timeProvider)
{
    public ResponseCacheStore(IOptions<ResponseCacheBudgetOptions> options) : this(options, TimeProvider.System) { }
    internal DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    internal TimeSpan Age(Entry entry) => timeProvider.GetElapsedTime(entry.StoredTimestamp);
    internal const int HeaderAllowance = 64 * 1024;
    private const int EntryOverhead = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _allocated;
    private long _generation = -1;
    public long AllocatedBytes { get { lock (_sync) return _allocated; } }
    public long SizeLimitBytes => Math.Max(0, options.Value.SizeLimitBytes);

    internal void SetGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation <= _generation) return;
            foreach (var key in _entries.Keys.ToArray()) Remove(key);
            _generation = generation;
        }
    }

    internal Capture? Reserve(int capacity)
    {
        if (capacity <= 0) return null;
        var charge = (long)capacity + HeaderAllowance + EntryOverhead;
        lock (_sync)
        {
            if (charge > SizeLimitBytes) return null;
            while (_allocated + charge > SizeLimitBytes && _lru.First is { } first) Remove(first.Value);
            if (_allocated + charge > SizeLimitBytes) return null;
            _allocated += charge;
        }
        try { return new Capture(this, new byte[capacity], charge); }
        catch { Release(charge); throw; }
    }

    internal ReadLease? Find(string key, TimeSpan? maxAge, TimeSpan? minFresh)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            var elapsed = Age(entry);
            if (elapsed >= entry.Lifetime) { Remove(key); return null; }
            if ((maxAge is { } age && entry.InitialAge + elapsed > age) ||
                (minFresh is { } fresh && entry.Lifetime - elapsed < fresh)) return null;
            _lru.Remove(entry.Node!); _lru.AddLast(entry.Node!);
            entry.Readers++;
            return new ReadLease(this, entry);
        }
    }

    internal bool Store(Capture capture, string key, long generation, int status, Dictionary<string, string> headers,
        int length, TimeSpan lifetime, TimeSpan initialAge)
    {
        lock (_sync)
        {
            if (generation != _generation || capture.Buffer == null) return false;
            Remove(key);
            var entry = new Entry(capture.Buffer, length, status, headers, capture.Charge, lifetime, initialAge, timeProvider);
            capture.Transfer();
            entry.Node = _lru.AddLast(key);
            _entries[key] = entry;
            return true;
        }
    }

    private void Remove(string key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node!); entry.Node = null;
        if (entry.Readers == 0) _allocated -= entry.Charge;
    }
    private void Release(long bytes) { lock (_sync) _allocated -= bytes; }
    private void ReleaseRead(Entry entry)
    {
        lock (_sync) { if (--entry.Readers == 0 && entry.Node == null) _allocated -= entry.Charge; }
    }

    internal sealed class Entry(byte[] body, int length, int status, Dictionary<string, string> headers,
        long charge, TimeSpan lifetime, TimeSpan initialAge, TimeProvider clock)
    {
        public byte[] Body { get; } = body;
        public int Length { get; } = length;
        public int Status { get; } = status;
        public IReadOnlyDictionary<string, string> Headers { get; } = headers;
        public long Charge { get; } = charge;
        public DateTimeOffset StoredAt { get; } = clock.GetUtcNow();
        public long StoredTimestamp { get; } = clock.GetTimestamp();
        public TimeSpan Lifetime { get; } = lifetime;
        public DateTimeOffset Expires { get; } = clock.GetUtcNow() + lifetime;
        public TimeSpan InitialAge { get; } = initialAge;
        public LinkedListNode<string>? Node { get; set; }
        public int Readers { get; set; }
    }
    internal sealed class ReadLease(ResponseCacheStore owner, Entry entry) : IDisposable
    {
        private bool _disposed;
        public Entry Value => entry;
        public void Dispose() { if (!_disposed) { _disposed = true; owner.ReleaseRead(entry); } }
    }
    internal sealed class Capture(ResponseCacheStore owner, byte[] buffer, long charge) : IDisposable
    {
        public byte[]? Buffer { get; private set; } = buffer;
        public long Charge => charge;
        public void Transfer() => Buffer = null;
        public void Dispose() { if (Buffer != null) { Buffer = null; owner.Release(charge); } }
    }
}
