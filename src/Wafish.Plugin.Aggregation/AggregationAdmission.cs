namespace Wafish.Plugin.Aggregation;

/// <summary>Shared process admission limits. Reservations include both response buffers and returned byte arrays.</summary>
public sealed class AggregationAdmission
{
    public const int MaxActive=16;
    public const long MaxBufferBytes=64L*1024*1024;
    private readonly object _gate=new();
    private int _active;
    private long _bytes;
    public IDisposable? TryAcquire(long bytes)
    {
        lock(_gate)
        {
            if(bytes<=0 || bytes>MaxBufferBytes || _active>=MaxActive || bytes>MaxBufferBytes-_bytes) return null;
            _active++;_bytes+=bytes;return new Reservation(this,bytes);
        }
    }
    private sealed class Reservation(AggregationAdmission owner,long bytes):IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0) return;
            lock(owner._gate) {owner._active--;owner._bytes-=bytes;}
        }
    }
}
