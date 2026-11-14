using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wafish.Plugin.RateLimit.Redis;

internal static class RedisRecoveryChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Redis recovery: failed construction backs off and concurrent callers share replacement", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var attempts = 0;
            var pending = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
            var logger = new CaptureLogger();
            await using var store = new RedisLuaRateLimitStore(logger, null, clock, _ =>
                Interlocked.Increment(ref attempts) == 1 ? throw new IOException("password=seeded-secret") : pending.Task);
            Check(!(await Acquire(store)).Available, "Construction failure did not report unavailable.");
            clock.ShiftUtc(TimeSpan.FromDays(1));
            clock.Advance(TimeSpan.FromMilliseconds(999));
            await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Acquire(store)));
            Check(attempts == 1, "Failure backoff used wall time or retried early.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var callers = Enumerable.Range(0, 64).Select(_ => Acquire(store)).ToArray();
            using var cancelled = new CancellationTokenSource();
            var cancelledCaller = Acquire(store, cancelled.Token);
            cancelled.Cancel();
            await ExpectCancellation(cancelledCaller);
            Check(attempts == 2 && callers.All(c => !c.IsCompleted), "Callers did not share one replacement.");
            pending.SetResult(connection);
            Check((await Task.WhenAll(callers)).All(r => r.Available && r.Allowed), "Replacement did not recover.");
            await store.DisposeAsync();
            Check(((ConnectionProxy)connection).Disposed == 1, "Successful replacement was not disposed exactly once.");
            Check(!(await Acquire(store)).Available && attempts == 2, "Disposed store created another connection.");
            Check(logger.Messages.All(m => !m.Contains("seeded-secret")), "Failure logging disclosed connection secrets.");
        });

        await run("Redis recovery: asynchronous failures use capped backoff and shutdown owns pending construction", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var attempts = 0;
            var pending = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, null, clock, _ =>
            { attempts++; return pending.Task; });
            try
            {
                foreach (var delay in new[] { 1, 2, 4, 8, 16, 30, 30 })
                {
                    var call = Acquire(store);
                    pending.SetException(new IOException("failure"));
                    Check(!(await call).Available, "Asynchronous failure was accepted.");
                    var before = attempts;
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    clock.Advance(TimeSpan.FromSeconds(delay).Subtract(TimeSpan.FromMilliseconds(1)));
                    await Acquire(store);
                    Check(attempts == before, "Construction retried before its bounded delay.");
                    clock.Advance(TimeSpan.FromMilliseconds(1));
                }
                var last = Acquire(store);
                var disposing = store.DisposeAsync().AsTask();
                Check(!disposing.IsCompleted, "Shutdown abandoned pending construction.");
                var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
                pending.SetResult(connection);
                await last;
                await disposing;
                Check(((ConnectionProxy)connection).Disposed == 1, "Late connection leaked during shutdown.");
            }
            finally { pending.TrySetException(new IOException("cleanup")); await store.DisposeAsync(); }
        });

        await run("Redis recovery: interrupted commands do not replace or replay a healthy connection", async () =>
        {
            var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
            var database = (DatabaseProxy)((ConnectionProxy)connection).Database;
            var attempts = 0;
            await using var store = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, null,
                new ReviewReleaseChecks.ManualTime(), _ => { attempts++; return Task.FromResult(connection); });
            var pending = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            database.Execute = () => pending.Task;
            using var cancellation = new CancellationTokenSource();
            var interrupted = Acquire(store, cancellation.Token);
            cancellation.Cancel();
            await ExpectCancellation(interrupted);
            pending.SetException(new IOException("interrupted"));
            database.Execute = () => Task.FromException<RedisResult>(new IOException("offline"));
            Check(!(await Acquire(store)).Available, "Transport failure was accepted.");
            database.Execute = () => Task.FromResult(RedisResult.Create(new RedisValue[] { 1, 9, 0 }));
            Check((await Acquire(store)).Allowed && attempts == 1 && database.Calls == 3, "Interrupted Lua was replayed or replaced the multiplexer.");
        });

        await run("Redis recovery: connection admission remains bounded and missing settings fail closed", async () =>
        {
            var connections = new List<IConnectionMultiplexer>();
            var clock = new ReviewReleaseChecks.ManualTime();
            await using var store = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance,
                Options.Create(new RedisRateLimitStoreOptions()), clock, _ =>
                { var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>(); connections.Add(connection); return Task.FromResult(connection); });
            Check(!(await store.TryAcquireAsync("FixedWindow", "key", 1, 10, 0, null)).Available, "Missing settings were accepted.");
            Check(!(await store.TryAcquireAsync("FixedWindow", "key", 0, 10, 0, "host")).Available, "Invalid limits were accepted.");
            for (var i = 0; i < 32; i++) { Check((await store.TryAcquireAsync("FixedWindow", "key", 1, 10, 0, "host" + i)).Allowed, "Connection admission failed early."); }
            Check(!(await store.TryAcquireAsync("FixedWindow", "key", 1, 10, 0, "overflow")).Available && connections.Count == 32, "Connection bound failed.");
            await store.DisposeAsync();
            Check(connections.All(c => ((ConnectionProxy)c).Disposed == 1), "Admitted connection leaked.");
        });
    }

    private static Task<DistributedRateLimitResult> Acquire(RedisLuaRateLimitStore store, CancellationToken ct = default)
        => store.TryAcquireAsync("FixedWindow", "key", 10, 30, 0, "host,password=seeded-secret", ct).AsTask();
    private static async Task ExpectCancellation(Task task)
    {
        try { await task; throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool value, string message) { if (!value) { throw new Exception(message); } }

    public class ConnectionProxy : DispatchProxy
    {
        public IDatabase Database { get; } = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        public int Disposed;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetDatabase" => Database,
            "DisposeAsync" => DisposeConnection(),
            _ => throw new NotSupportedException(method?.Name)
        };
        private ValueTask DisposeConnection() { Interlocked.Increment(ref Disposed); return ValueTask.CompletedTask; }
    }
    public class DatabaseProxy : DispatchProxy
    {
        public Func<Task<RedisResult>> Execute = () => Task.FromResult(RedisResult.Create(new RedisValue[] { 1, 9, 0 }));
        public int Calls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "ScriptEvaluateAsync") { throw new NotSupportedException(method?.Name); }
            Interlocked.Increment(ref Calls);
            return Execute();
        }
    }
    private sealed class CaptureLogger : ILogger<RedisLuaRateLimitStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
            => Messages.Add(format(state, exception));
    }
}
