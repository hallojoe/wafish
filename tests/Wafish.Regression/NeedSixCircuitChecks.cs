using Wafish.Dashboard.Infrastructure.State;
using Wafish.Models;

internal static class NeedSixCircuitChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Circuit jitter: recovery deadlines spread once within bounded monotonic delays", () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var random = new SequenceRandom();
            var store = new InMemoryCircuitStateStore(clock, random);
            var config = new CircuitBreakerConfig { MinimumThroughput = 1, RecoveryTimeoutSeconds = 10, RecoveryJitterSeconds = 4 };
            foreach (var id in new[] { "a", "b", "c" }) store.RecordOutcome("cluster", id, true, config: config);
            Check(random.Calls == 3, "Jitter was not sampled once per opening.");
            clock.ShiftUtc(TimeSpan.FromDays(2));
            clock.Advance(TimeSpan.FromSeconds(10));
            using var first = store.TryAcquireAttempt("cluster", "a", config);
            Check(first is not null && store.IsCircuitOpen("cluster", "b") && store.IsCircuitOpen("cluster", "c"), "Recovery deadlines did not spread.");
            config.RecoveryTimeoutSeconds = 100;
            config.RecoveryJitterSeconds = 300;
            for (var i = 0; i < 20; i++) Check(!store.TryAcquire("cluster", "b", config: config), "Circuit admitted an early probe.");
            Check(random.Calls == 3, "Polling redrew the recovery delay.");
            clock.Advance(TimeSpan.FromSeconds(2));
            using var second = store.TryAcquireAttempt("cluster", "b", config);
            Check(second is not null && store.TryAcquireAttempt("cluster", "b", config) is null, "Deadline changed or probe limit failed.");
            clock.Advance(TimeSpan.FromSeconds(2));
            using var third = store.TryAcquireAttempt("cluster", "c", config);
            Check(third is not null, "Recovery exceeded the original maximum delay.");
            second!.Complete(true);
            Check(random.Calls == 4 && store.IsCircuitOpen("cluster", "b"), "Failed probe did not start a new bounded delay.");
            return Task.CompletedTask;
        });
        await run("Circuit jitter: zero preserves recovery timing and invalid values are bounded", () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var random = new SequenceRandom();
            var store = new InMemoryCircuitStateStore(clock, random);
            var config = new CircuitBreakerConfig { MinimumThroughput = 1, RecoveryTimeoutSeconds = 1 };
            store.RecordOutcome("cluster", "a", true, config: config);
            Check(random.Calls == 0 && store.IsCircuitOpen("cluster", "a"), "Default jitter changed timing.");
            clock.Advance(TimeSpan.FromSeconds(1));
            using var probe = store.TryAcquireAttempt("cluster", "a", config);
            Check(probe is not null, "Default recovery did not admit a probe.");
            Check(new CircuitState(new() { RecoveryJitterSeconds = -1 }).RecoveryJitterSeconds == 0 &&
                new CircuitState(new() { RecoveryJitterSeconds = int.MaxValue }).RecoveryJitterSeconds == 300, "Jitter bounds failed.");
            return Task.CompletedTask;
        });
    }
    private sealed class SequenceRandom : Random
    {
        public int Calls { get; private set; }
        public override double NextDouble() => (Calls++ % 3) * 0.49;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
