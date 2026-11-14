namespace Wafish.Plugin.Cache;

public sealed partial class ResponseCacheStore
{
    private const int MaximumFills = 256;
    private const int MaximumWaiters = 1024;
    private readonly Dictionary<string, TaskCompletionSource> _fills = new(StringComparer.Ordinal);
    private int _waiters;
    internal TimeProvider Clock => timeProvider;
    internal (int Owners, int Waiters) FillCounts { get { lock (_sync) return (_fills.Count, _waiters); } }

    internal FillLease? JoinFill(string key)
    {
        lock (_sync)
        {
            if (_fills.TryGetValue(key, out var existing))
            {
                if (_waiters >= MaximumWaiters) return null;
                _waiters++;
                return new(this, key, existing, false);
            }
            if (_fills.Count >= MaximumFills) return null;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _fills.Add(key, completion);
            return new(this, key, completion, true);
        }
    }

    internal sealed class FillLease(ResponseCacheStore store, string key, TaskCompletionSource completion, bool owner) : IDisposable
    {
        private int _disposed;
        public bool IsOwner => owner;
        public Task Completion => completion.Task;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (store._sync)
            {
                if (!owner) { store._waiters--; return; }
                store._fills.Remove(key);
                completion.TrySetResult();
            }
        }
    }
}
