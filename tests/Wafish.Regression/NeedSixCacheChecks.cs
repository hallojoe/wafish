using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wafish.Plugin.Cache;

internal static class NeedSixCacheChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Cache fills: concurrent public misses share one fill and cancelled waiters leave the owner running", async () =>
        {
            var fixture = new ResponseStreamingChecks.Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Respond = async c => { entered.SetResult(); await release.Task; await c.Response.WriteAsync("shared"); };
            var owner = fixture.Send(); await entered.Task;
            using var cancel = new CancellationTokenSource();
            var cancelled = fixture.Send(c => c.RequestAborted = cancel.Token);
            var peers = Enumerable.Range(0, 12).Select(_ => fixture.Send()).ToArray();
            Check(fixture.Store.FillCounts == (1, 13), "Fill ownership or waiter count changed.");
            cancel.Cancel();
            try { await cancelled; throw new Exception("Waiter ignored cancellation."); } catch (OperationCanceledException) { }
            Check(!owner.IsCompleted && fixture.Calls == 1, "Waiter cancellation interrupted the owner.");
            release.SetResult(); await owner; await Task.WhenAll(peers);
            Check(fixture.Calls == 1 && fixture.Store.FillCounts == (0, 0), "Public fills duplicated upstream work or leaked slots.");
        });
        await run("Cache fills: private, failed, purged, and retired responses cannot populate waiting requests", async () =>
        {
            foreach (var mode in new[] { "private", "failed", "purged", "generation" })
            {
                var fixture = new ResponseStreamingChecks.Fixture();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Respond = async c =>
                {
                    if (fixture.Calls == 1)
                    {
                        entered.SetResult(); await release.Task;
                        if (mode == "failed") throw new IOException("upstream failed");
                        if (mode == "private") c.Response.Headers.CacheControl = "private";
                    }
                    await c.Response.WriteAsync("call-" + fixture.Calls);
                };
                var owner = fixture.Send(); await entered.Task; var waiter = fixture.Send();
                if (mode == "purged") fixture.Store.Purge("test");
                if (mode == "generation") { fixture.Publish(2); await fixture.Send(c => c.Request.Method = "HEAD"); }
                release.SetResult();
                try { await owner; } catch (IOException) when (mode == "failed") { }
                await waiter;
                Check(fixture.Calls == (mode == "generation" ? 3 : 2) && fixture.Store.FillCounts == (0, 0), mode + " exposed a stale or private fill.");
            }
        });
        await run("Cache fills: waits and admission are bounded independently of body capture", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var store = new ResponseCacheStore(Options.Create(new ResponseCacheBudgetOptions()), clock);
            var fixture = new ResponseStreamingChecks.Fixture(sharedStore: store);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Respond = async c => { if (fixture.Calls == 1) { entered.SetResult(); await release.Task; } await c.Response.WriteAsync("ok"); };
            var owner = fixture.Send(); await entered.Task; var waiter = fixture.Send();
            clock.Advance(TimeSpan.FromSeconds(1)); await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!owner.IsCompleted && fixture.Calls == 2, "Wait timeout failed to release independent upstream work.");
            release.SetResult(); await owner;
            var owners = Enumerable.Range(0, 256).Select(i => store.JoinFill(i.ToString())!).ToArray();
            Check(store.JoinFill("overflow") is null, "Fill owners exceeded the bound.");
            var waiters = Enumerable.Range(0, 1024).Select(_ => store.JoinFill("0")!).ToArray();
            Check(store.JoinFill("0") is null, "Waiters exceeded the bound.");
            foreach (var item in waiters.Concat(owners)) { item.Dispose(); item.Dispose(); }
            Check(store.FillCounts == (0, 0), "Admission slots leaked.");
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
