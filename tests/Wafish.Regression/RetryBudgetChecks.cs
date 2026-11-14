using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Plugin.Retry;
using Fixture = RetryStreamingChecks.Fixture;

internal static class RetryBudgetChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Retry budget: concurrent routes share allowance and preserve initial attempts", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var budget = new ClusterRetryBudget(Options.Create(new ClusterRetryBudgetOptions { RetriesPerWindow = 3 }), clock);
            using var services = new ServiceCollection().AddSingleton(budget).BuildServiceProvider();
            var one = new Fixture(); var two = new Fixture();
            one.Respond = two.Respond = c => { c.Response.StatusCode = 503; return Task.CompletedTask; };
            await Parallel.ForEachAsync(Enumerable.Range(0, 20), async (i, _) =>
            {
                var fixture = i % 2 == 0 ? one : two;
                var context = fixture.Context(); context.RequestServices = services;
                await fixture.Pipeline(context);
                if (context.Response.StatusCode != 503) throw new Exception("Budget denial changed upstream failure.");
            });
            Require(one.Calls + two.Calls == 23 && budget.DeniedRetries == 17, "Budget did not bound extra attempts.");
            clock.Advance(TimeSpan.FromSeconds(1));
            Require(budget.TryAcquire("cluster") && budget.TryAcquire("other"), "Window or cluster isolation failed.");
            for (var i = 0; i < 10010; i++) budget.TryAcquire("cluster-" + i);
            Require(budget.RetainedClusters == 10000 && !budget.TryAcquire(null), "Cluster retention exceeded bound.");
            clock.Advance(TimeSpan.FromSeconds(1));
            Require(budget.TryAcquire("new") && budget.RetainedClusters == 1, "Expired identities were retained.");
            var zero = new ClusterRetryBudget(Options.Create(new ClusterRetryBudgetOptions { RetriesPerWindow = 0 }));
            Require(!zero.TryAcquire("any"), "Zero allowance admitted retries.");
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
