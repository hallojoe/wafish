using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Models;
using Fixture = RetryStreamingChecks.Fixture;

internal static class RetryTimingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Retry timing: upstream delta, date, invalid, and capped delays use controlled time", async () =>
        {
            foreach (var kind in new[] { "delta", "date", "invalid", "capped", "past" })
            {
                var clock = new ReviewReleaseChecks.ManualTime();
                using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddSingleton<Random>(new FixedRandom()).BuildServiceProvider();
                var fixture = new Fixture(policy: Policy().WithValues(200, 10));
                var context = fixture.Context(); context.RequestServices = services;
                fixture.Respond = c =>
                {
                    c.Response.StatusCode = fixture.Calls == 1 ? 503 : 200;
                    if (fixture.Calls == 1)
                    {
                        c.Response.Headers.RetryAfter = kind switch
                        {
                            "delta" => "2", "date" => clock.GetUtcNow().AddSeconds(2).ToString("r"), "capped" => "99999",
                            "past" => clock.GetUtcNow().AddSeconds(-1).ToString("r"), _ => "invalid"
                        };
                    }
                    return Task.CompletedTask;
                };
                var pipeline = fixture.Pipeline(context);
                var delay = kind switch { "delta" or "date" => 2000, "capped" => 3000, _ => 207 };
                clock.Advance(TimeSpan.FromMilliseconds(delay - 1));
                Check(fixture.Calls == 1 && !pipeline.IsCompleted, "Retry ran before its required delay.");
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await pipeline.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Calls == 2 && context.Response.StatusCode == 200, "Delayed retry did not recover.");
                Check(context.Response.Headers["X-Retry-Count"] == "1" && clock.TimerCount == 0, "Retry count or timer cleanup failed.");
            }
        });

        await run("Retry timing: total deadline interrupts upstream Retry-After without another attempt", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
            var policy = Policy(); policy.TotalTimeoutSeconds = 2; policy.MaxRetryAfterSeconds = 60;
            var fixture = new Fixture(policy: policy);
            var context = fixture.Context(); context.RequestServices = services;
            fixture.Respond = c => { c.Response.StatusCode = 503; c.Response.Headers.RetryAfter = "60"; return Task.CompletedTask; };
            var response = context.Response.Body;
            var task = fixture.Pipeline(context);
            clock.Advance(TimeSpan.FromSeconds(2));
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(context.Response.StatusCode == 504 && fixture.Calls == 1, "Total deadline allowed another attempt.");
            Check(!context.Response.Headers.ContainsKey("Retry-After") && !context.Response.Headers.ContainsKey("X-Retry-Count"), "Timeout retained stale retry headers.");
            Check(ReferenceEquals(response, context.Response.Body) && clock.TimerCount == 0, "Deadline leaked response features or timers.");
        });

        await run("Retry timing: attempt limits and exponential jitter are deterministic", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddSingleton<Random>(new FixedRandom()).BuildServiceProvider();
            var policy = Policy(); policy.MaxRetries = 3; policy.BackoffBaseMs = 100; policy.BackoffJitterMs = 10;
            var fixture = new Fixture(policy: policy);
            var context = fixture.Context(); context.RequestServices = services;
            fixture.Respond = c => { c.Response.StatusCode = 503; return Task.CompletedTask; };
            var task = fixture.Pipeline(context);
            for (var retry = 1; retry <= 3; retry++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100 * (1 << (retry - 1)) + 6));
                Check(fixture.Calls == retry, "Backoff ran early.");
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await Until(() => fixture.Calls == retry + 1);
            }
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Calls == 4 && context.Response.StatusCode == 503, "Attempt ceiling changed.");
        });

        await run("Retry timing: caller cancellation stays distinct from deadline expiry", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
            var fixture = new Fixture(policy: Policy());
            var context = fixture.Context(); context.RequestServices = services;
            using var cancellation = new CancellationTokenSource();
            context.RequestAborted = cancellation.Token;
            fixture.Respond = async c => { await Task.Delay(Timeout.Infinite, c.RequestAborted); };
            var task = fixture.Pipeline(context);
            cancellation.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Caller cancellation was swallowed."); }
            catch (OperationCanceledException) { }
            Check(context.RequestAborted == cancellation.Token && context.Response.StatusCode != 504 && fixture.Calls == 1,
                "Caller cancellation became a retry or gateway timeout.");
        });

        await run("Retry timing: body preparation and non-idempotent forwarding obey the total deadline", async () =>
        {
            foreach (var body in new[] { true, false })
            {
                var clock = new ReviewReleaseChecks.ManualTime();
                using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
                var policy = Policy(); policy.TotalTimeoutSeconds = 1; policy.RetryNonIdempotent = false;
                var fixture = new Fixture(policy: policy);
                var context = fixture.Context(); context.RequestServices = services;
                var original = new BlockingBody();
                if (body) { context.Request.Body = original; context.Request.ContentLength = 10; }
                else { context.Request.Method = "POST"; fixture.Respond = async c => await Task.Delay(Timeout.Infinite, c.RequestAborted); }
                var task = fixture.Pipeline(context);
                clock.Advance(TimeSpan.FromSeconds(1));
                await task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(context.Response.StatusCode == 504 && fixture.Calls == (body ? 0 : 1), "Total deadline did not cover all request work.");
                if (body) { Check(ReferenceEquals(context.Request.Body, original), "Preparation leaked its request stream."); }
            }
        });
    }
    private static RequestRetryBindingOptions Policy() => new() { MaxRetries = 1, BackoffBaseMs = 0, BackoffJitterMs = 0,
        TimeoutSeconds = 30, TotalTimeoutSeconds = 120, MaxRetryAfterSeconds = 3 };
    private static RequestRetryBindingOptions WithValues(this RequestRetryBindingOptions policy, int backoff, int jitter)
    { policy.BackoffBaseMs = backoff; policy.BackoffJitterMs = jitter; return policy; }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(1, timeout.Token); }
    }
    private static void Check(bool value, string message) { if (!value) { throw new Exception(message); } }
    private sealed class FixedRandom : Random { public override int Next(int maxValue) => Math.Min(7, maxValue - 1); }
    private sealed class BlockingBody : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
