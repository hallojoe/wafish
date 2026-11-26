using System.Text.Json.Nodes;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task PerformanceBudgetsAsync()
    {
        var report = JsonNode.Parse("""
        {"schemaVersion":5,"settings":{"suite":"baseline","repetitions":3,"processMeasurementsAvailable":true},
         "environment":{"runtime":"test","operatingSystem":"test","cpuModel":"test","logicalProcessors":1,"processArchitecture":"X64","osArchitecture":"X64","serverGc":false},"transport":{},"arrivals":{},"latency":{},"pluginSets":[],"acceptEncoding":"identity","hosts":[],"results":[]}
        """)!;
        foreach (var name in new[] { "Backend", "native" })
            report["hosts"]!.AsArray().Add(new JsonObject { ["name"] = name, ["manifest"] = new JsonObject { ["configurationHash"] = new string('a',64) } });
        for (var i = 1; i <= 3; i++) report["results"]!.AsArray().Add(JsonNode.Parse($$$"""
        {"gateway":"native","scenario":"plain","repetition":{{{i}}},"configurationHash":"{{{new string('a',64)}}}",
        "requests":100,"completedResponses":100,"failures":0,"transportFailures":0,"requestsPerSecond":100,
        "p95Ms":10,"p99Ms":12,"workingSetMb":30,"firstByteP95Ms":1,"firstByteSamples":100,"scheduledP95Ms":10,
        "arrivals":{"offered":100,"dropped":0,"cancelledBeforeStart":0}}
        """));
        var policy = new BenchmarkBudgetPolicy(3, 20, [new("requestsPerSecond", 10), new("p95Ms", 10, 1), new("droppedPercent", 0, 1),
            new("p99Ms", 10), new("workingSetMb", 10), new("firstByteP95Ms", 10), new("scheduledP95Ms", 10)]);
        BenchmarkBudgetResult Compare(Action<JsonNode> change)
        {
            var candidate = report.DeepClone(); change(candidate); return BenchmarkBudget.Compare(report, candidate, policy);
        }
        void Set(JsonNode doc, string key, double value) { foreach (var row in doc["results"]!.AsArray()) row![key] = value; }
        Check(Compare(_ => { }).ExitCode == 0, "Identical stable runs failed.");
        Check(Compare(c => Set(c, "requestsPerSecond", 80)).ExitCode == 2, "Throughput regression passed.");
        Check(Compare(c => Set(c, "p95Ms", 15)).ExitCode == 2, "Latency regression passed.");
        Check(Compare(c => Set(c, "p95Ms", 10.9)).ExitCode == 0, "Within-budget latency failed.");
        Check(Compare(c => c["results"]![0]!["p95Ms"] = 100).ExitCode == 3, "Unstable repetitions passed.");
        Check(Compare(c => { Set(c, "p95Ms", 11.5); c["results"]![0]!["p95Ms"] = 10; }).ExitCode == 3, "Overlapping distributions became a regression.");
        Check(Compare(c => { Set(c, "failures", 1); Set(c, "p95Ms", 50); }).ExitCode == 1, "Request failures lost precedence.");
        Check(Compare(c => Set(c, "completedResponses", 0)).ExitCode == 1, "Empty responses passed.");
        Check(Compare(c => { foreach (var row in c["results"]!.AsArray()) row!["arrivals"]!["dropped"] = 5; }).ExitCode == 2, "Dropped arrivals escaped budgets.");
        foreach (var mutation in new Action<JsonNode>[] {
            c => c["schemaVersion"] = 4, c => c["settings"]!["suite"] = "first", c => c["transport"] = "changed",
            c => c["environment"] = new JsonObject { ["cpuModel"] = "different" }, c => c["results"]![0]!["configurationHash"] = "bad",
            c => c["results"]![0]!["repetition"] = 2, c => c["results"]![0]!["gateway"] = "other",
            c => c["results"]!.AsArray().RemoveAt(0), c => c["results"]![0]!["p95Ms"] = -1,
            c => c["results"]![0]!["firstByteSamples"] = 0, c => c["results"]![0]!.AsObject().Remove("p95Ms") })
        {
            try { Compare(mutation); throw new Exception("Invalid comparison passed."); } catch (ArgumentException) { }
        }
        foreach (var invalid in new[] { policy with { MinimumRepetitions = 1 }, policy with { MaximumSpreadPercent = 0 },
            policy with { Metrics = [] }, policy with { Metrics = [new("unknown", 1)] },
            policy with { Metrics = [new("p95Ms", -1)] }, policy with { Metrics = [new("p95Ms", 1),new("p95Ms",1)] } })
        { try { BenchmarkBudget.Compare(report, report, invalid); throw new Exception("Invalid budgets passed."); } catch (ArgumentException) { } }
        var directory = Path.Combine(Path.GetTempPath(), "wafish-budgets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var baseline = Path.Combine(directory, "baseline.json"); var budgets = Path.Combine(directory, "budgets.json");
            var output = Path.Combine(directory, "comparison.json");
            await File.WriteAllTextAsync(baseline, report.ToJsonString());
            await File.WriteAllTextAsync(budgets, """{"minimumRepetitions":3,"maximumSpreadPercent":20,"metrics":[{"metric":"p95Ms","percent":10,"absolute":1}]}""");
            await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--compare-baseline=" + baseline, "--compare-candidate=" + baseline,
                "--budgets=" + budgets, "--comparison-output=" + output], ready: false);
            Check((await runner.FinishAsync()).ExitCode == 0 && JsonNode.Parse(await File.ReadAllTextAsync(output))!["status"]!.GetValue<string>() == "passed", "Comparison CLI failed.");
            Check(await BenchmarkBudget.RunAsync(["--compare-baseline=" + baseline, "--compare-candidate=" + baseline,
                "--budgets=" + Path.Combine(directory,"missing.json"), "--comparison-output=" + output]) == 4, "Missing policy did not report invalid input.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
