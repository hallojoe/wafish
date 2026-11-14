using Microsoft.Extensions.Options;
namespace Wafish.Plugin.Cache;

public sealed class ResponseCacheBudgetOptions
{
    public long SizeLimitBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>One budget covers stored entries, pending captures, and entries still being served after eviction.</summary>
public sealed partial class ResponseCacheStore(IOptions<ResponseCacheBudgetOptions> options, TimeProvider timeProvider)
{
    public ResponseCacheStore(IOptions<ResponseCacheBudgetOptions> options) : this(options, TimeProvider.System) { }
    internal DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    internal TimeSpan Age(Entry entry) => timeProvider.GetElapsedTime(entry.StoredTimestamp);
    internal const int HeaderAllowance = 64 * 1024;
    private const int EntryOverhead = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _allocated;
    private long _generation = -1;
    private long _purgeVersion;
    public long AllocatedBytes { get { lock (_sync) { return _allocated; } } }
    public long SizeLimitBytes => Math.Max(0, options.Value.SizeLimitBytes);

    internal void SetGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation <= _generation)
            {
                return;
            }

            foreach (var key in _entries.Keys.ToArray())
            {
                Remove(key);
            }

            _generation = generation;
        }
    }

    internal Capture? Reserve(int capacity)
    {
        if (capacity <= 0)
        {
            return null;
        }

        long purgeVersion;
        var charge = (long)capacity + HeaderAllowance + EntryOverhead;
        lock (_sync)
        {
            if (charge > SizeLimitBytes)
            {
                return null;
            }

            while (_allocated + charge > SizeLimitBytes && _lru.First is { } first)
            {
                Remove(first.Value);
            }

            if (_allocated + charge > SizeLimitBytes)
            {
                return null;
            }

            _allocated += charge;
            purgeVersion = _purgeVersion;
        }
        try { return new Capture(this, new byte[capacity], charge, purgeVersion); }
        catch { Release(charge); throw; }
    }

    internal ReadLease? Find(string key, TimeSpan? maxAge, TimeSpan? minFresh)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            var elapsed = Age(entry);
            if (elapsed >= entry.Lifetime) { Remove(key); return null; }
            if ((maxAge is { } age && entry.InitialAge + elapsed > age) ||
                (minFresh is { } fresh && entry.Lifetime - elapsed < fresh))
            {
                return null;
            }

            _lru.Remove(entry.Node!); _lru.AddLast(entry.Node!);
            entry.Readers++;
            return new ReadLease(this, entry);
        }
    }

    internal bool Store(Capture capture, string key, long generation, int status, Dictionary<string, string> headers,
        int length, TimeSpan lifetime, TimeSpan initialAge, string routeId = "", string[]? tags = null)
    {
        lock (_sync)
        {
            if (generation != _generation || capture.Buffer == null || capture.PurgeVersion != _purgeVersion || routeId.Length > 256 || tags is { Length: > 8 } || tags?.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 64) == true)
            {
                return false;
            }

            Remove(key);
            // Charge both arrays before allocating. Under pressure, keep the original capture.
            // Compaction must never borrow the reservation held by pending fills or active readers.
            if (length < capture.Buffer.Length / 2 && _allocated + length + 24 <= SizeLimitBytes)
            {
                var temporaryCharge = length + 24L;
                _allocated += temporaryCharge;
                try
                {
                    var compact = new byte[length];
                    capture.Buffer.AsSpan(0, length).CopyTo(compact);
                    var previousCharge = capture.Charge;
                    capture.Replace(compact, temporaryCharge + HeaderAllowance + EntryOverhead);
                    _allocated -= previousCharge - HeaderAllowance - EntryOverhead;
                }
                catch { _allocated -= temporaryCharge; throw; }
            }
            var entry = new Entry(capture.Buffer, length, status, headers, capture.Charge, lifetime, initialAge, timeProvider) { RouteId = routeId, Tags = tags?.ToArray() ?? [] };
            capture.Transfer();
            entry.Node = _lru.AddLast(key);
            _entries[key] = entry;
            return true;
        }
    }

    /// <summary>Invalidates matching entries and prevents pending fills from restoring purged responses.</summary>
    public int Purge(string? routeId = null, string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(routeId) && string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("A route or tag is required.");
        if (routeId?.Length > 256 || tag?.Length > 64) throw new ArgumentException("Purge selectors exceed their limits.");
        lock (_sync)
        {
            _purgeVersion++;
            var keys = _entries.Where(p => (routeId is null || string.Equals(p.Value.RouteId, routeId, StringComparison.OrdinalIgnoreCase))
                && (tag is null || p.Value.Tags.Contains(tag, StringComparer.Ordinal))).Select(p => p.Key).ToArray();
            foreach (var key in keys) Remove(key);
            return keys.Length;
        }
    }

    private void Remove(string key)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return;
        }

        _lru.Remove(entry.Node!); entry.Node = null;
        if (entry.Readers == 0)
        {
            _allocated -= entry.Charge;
        }
    }
    private void Release(long bytes) { lock (_sync)
        {
            _allocated -= bytes;
        }
    }
    private void ReleaseRead(Entry entry)
    {
        lock (_sync) { if (--entry.Readers == 0 && entry.Node == null)
            {
                _allocated -= entry.Charge;
            }
        }
    }

    internal sealed class Entry(byte[] body, int length, int status, Dictionary<string, string> headers,
        long charge, TimeSpan lifetime, TimeSpan initialAge, TimeProvider clock)
    {
        public string RouteId { get; init; } = "";
        public string[] Tags { get; init; } = [];
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
    internal sealed class Capture(ResponseCacheStore owner, byte[] buffer, long charge, long purgeVersion) : IDisposable
    {
        public long PurgeVersion { get; } = purgeVersion;
        public byte[]? Buffer { get; private set; } = buffer;
        public long Charge { get; private set; } = charge;
        public void Replace(byte[] replacement, long replacementCharge) { Buffer = replacement; Charge = replacementCharge; }
        public void Transfer() => Buffer = null;
        public void Dispose() { if (Buffer != null) { Buffer = null; owner.Release(Charge); } }
    }
}
