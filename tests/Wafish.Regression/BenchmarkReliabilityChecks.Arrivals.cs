using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task ScheduledArrivalsAsync()
    {
        var active = 0; var maximum = 0;
        var overloaded = await BenchmarkArrivalSchedule.RunAsync(1, TimeSpan.FromSeconds(1), 100, 2, async (_, token) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await Task.Delay(100, token);
            Interlocked.Decrement(ref active);
        });
        Check(overloaded.Offered == 100 && overloaded.Dropped > 50 && overloaded.Started < 20, "Slow work silently reduced offered load.");
        Check(maximum == 1 && overloaded.Started + overloaded.Dropped == 100 && overloaded.QueueP95Ms > 0, "Queue accounting or worker bound failed.");
        var fast = await BenchmarkArrivalSchedule.RunAsync(2, TimeSpan.FromSeconds(1), 10, 16, (_, _) => Task.CompletedTask);
        Check(fast.Offered == 10 && fast.Started == 10 && fast.Dropped == 0, "Unsaturated arrivals were lost.");
        var empty = await BenchmarkArrivalSchedule.RunAsync(1, TimeSpan.Zero, 0, 1, (_, _) => throw new Exception("Zero phase dispatched work."));
        Check(empty.Offered == 0, "Zero phase recorded arrivals.");
        using var cancel = new CancellationTokenSource();
        var stopped = await BenchmarkArrivalSchedule.RunAsync(1, TimeSpan.FromSeconds(1), 100, 2, async (_, token) =>
        {
            await Task.Delay(30, token); cancel.Cancel();
        }, cancel.Token);
        Check(stopped.Started == 1 && stopped.Started + stopped.Dropped + stopped.CancelledBeforeStart == stopped.Offered, "Cancelled queue did not drain or lost accounting.");
        foreach (var (rate, capacity) in new[] { (-1, 1), (100001, 1), (1, 0), (1, 65537) })
        {
            try { await BenchmarkArrivalSchedule.RunAsync(1, TimeSpan.Zero, rate, capacity, (_, _) => Task.CompletedTask); throw new Exception("Invalid schedule accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--external-url=" + backend.Address,
            "--path=/api/perf/delay/100", "--arrival-rate=100", "--arrival-queue=2", "--duration=1", "--warmup=0",
            "--concurrency=1", "--repetitions=1"], ready: false);
        var result = await runner.FinishAsync();
        Check(result.ExitCode == 0, "Scheduled external run failed: " + result.Output);
        var reportPath = Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.json").Single();
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        var row = report.RootElement.GetProperty("results")[0]; var arrival = row.GetProperty("arrivals");
        Check(arrival.GetProperty("offered").GetInt64() == 100 && arrival.GetProperty("dropped").GetInt64() > 0, "Report concealed overload.");
        Check(row.GetProperty("completedResponses").GetInt64() == arrival.GetProperty("started").GetInt64() && row.GetProperty("failures").GetInt64() == 0, "Completed responses differ from successful dispatches.");
        Check(row.GetProperty("scheduledP95Ms").GetDouble() >= row.GetProperty("p95Ms").GetDouble(), "Scheduled latency omitted queue delay.");
        Check((await File.ReadAllTextAsync(Path.ChangeExtension(reportPath, ".csv"))).Contains("CancelledBeforeStart"), "CSV omitted schedule counts.");
        Check((await File.ReadAllTextAsync(Path.ChangeExtension(reportPath, ".md"))).Contains("Queue P95"), "Markdown omitted queue latency.");
    }
}
