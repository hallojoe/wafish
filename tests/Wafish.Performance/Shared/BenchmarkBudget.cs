using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wafish.Performance;

internal sealed record BenchmarkMetricBudget(string Metric, double Percent, double Absolute = 0);
internal sealed record BenchmarkBudgetPolicy(int MinimumRepetitions, double MaximumSpreadPercent, BenchmarkMetricBudget[] Metrics);
internal sealed record BenchmarkBudgetCheck(string Gateway, string Scenario, string Metric, double Baseline, double Candidate,
    double AllowedChange, double BaselineSpreadPercent, double CandidateSpreadPercent, string Status);
internal sealed record BenchmarkBudgetResult(string Status, int ExitCode, string[] Reasons, BenchmarkBudgetCheck[] Checks);

internal static class BenchmarkBudget
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string[] Metrics = ["requestsPerSecond", "p95Ms", "p99Ms", "workingSetMb", "firstByteP95Ms", "scheduledP95Ms", "droppedPercent"];

    public static async Task<int> RunAsync(string[] args)
    {
        var output = BenchmarkOptions.Read(args, "--comparison-output", "performance-comparison.json");
        BenchmarkBudgetResult result;
        try
        {
            var baseline = Read(BenchmarkOptions.Read(args, "--compare-baseline", ""));
            var candidate = Read(BenchmarkOptions.Read(args, "--compare-candidate", ""));
            var policy = Read(BenchmarkOptions.Read(args, "--budgets", "")).Deserialize<BenchmarkBudgetPolicy>(Json)
                ?? throw new ArgumentException("Budget policy is empty.");
            result = Compare(baseline, candidate, policy);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            result = new("invalid", 4, [error.Message], []);
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"Performance budget: {result.Status}. Report: {output}");
        foreach (var reason in result.Reasons) Console.WriteLine(reason);
        return result.ExitCode;
    }

    private static JsonNode Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ArgumentException("Comparison input exceeds 16 MiB.");
        return JsonNode.Parse(File.ReadAllText(path)) ?? throw new ArgumentException("Empty comparison input.");
    }

    public static BenchmarkBudgetResult Compare(JsonNode baseline, JsonNode candidate, BenchmarkBudgetPolicy policy)
    {
        if (policy.MinimumRepetitions is < 3 or > 100 || !double.IsFinite(policy.MaximumSpreadPercent) || policy.MaximumSpreadPercent is <= 0 or > 100)
            throw new ArgumentException("Require 3..100 repetitions and a positive spread limit up to 100 percent.");
        if (policy.Metrics is not { Length: > 0 and <= 7 } || policy.Metrics.Any(m => m is null || !Metrics.Contains(m.Metric) ||
            !double.IsFinite(m.Percent) || m.Percent is < 0 or > 100 || !double.IsFinite(m.Absolute) || m.Absolute < 0) ||
            policy.Metrics.Select(m => m.Metric).Distinct().Count() != policy.Metrics.Length)
            throw new ArgumentException("Select distinct supported metrics with finite, nonnegative tolerances; percent cannot exceed 100.");
        foreach (var report in new[] { baseline, candidate })
        {
            if (report["schemaVersion"]?.GetValue<int>() != 5 || report["settings"]?["suite"]?.GetValue<string>() is not ("baseline" or "plugins"))
                throw new ArgumentException("Budgets require managed baseline or plugin reports with schema version 5.");
        }
        foreach (var field in new[] { "settings", "transport", "streaming", "arrivals", "latency", "pluginSets", "acceptEncoding" })
            RequireEqual(baseline[field], candidate[field], field);
        foreach (var field in new[] { "runtime", "operatingSystem", "cpuModel", "logicalProcessors", "processArchitecture", "osArchitecture", "serverGc" })
        {
            if (baseline["environment"]?[field] is null || candidate["environment"]?[field] is null)
                throw new ArgumentException("Missing environment field: " + field);
            RequireEqual(baseline["environment"]?[field], candidate["environment"]?[field], "environment." + field);
        }
        foreach (var report in new[] { baseline, candidate })
        {
            foreach (var field in new[] { "settings", "transport", "arrivals", "latency", "pluginSets", "acceptEncoding", "environment", "hosts" })
                if (report[field] is null) throw new ArgumentException("Missing report field: " + field);
            if (report["settings"]!["processMeasurementsAvailable"]?.GetValue<bool>() != true)
                throw new ArgumentException("Managed process measurements are required.");
        }
        RequireEqual(HostHashes(baseline), HostHashes(candidate), "host configurations");
        var left = Rows(baseline, policy.MinimumRepetitions);
        var right = Rows(candidate, policy.MinimumRepetitions);
        if (!left.Keys.ToHashSet().SetEquals(right.Keys)) throw new ArgumentException("Gateway/scenario sets differ.");
        var failures = new List<string>();
        var checks = new List<BenchmarkBudgetCheck>();
        foreach (var (key, before) in left)
        {
            var after = right[key];
            if (before.Length != after.Length) throw new ArgumentException("Repetition counts differ.");
            RequireEqual(before[0]["configurationHash"], after[0]["configurationHash"], "configurationHash: " + key);
            foreach (var (label, rows) in new[] { ("baseline", before), ("candidate", after) })
            {
                if (rows.Any(row => Number(row, "requests") <= 0 || Number(row, "completedResponses") <= 0 || Number(row, "failures") > 0 || Number(row, "transportFailures") > 0))
                    failures.Add($"{label} {key}: request failure or no complete responses.");
            }
            foreach (var budget in policy.Metrics)
            {
                var oldValues = before.Select(row => Value(row, budget.Metric)).Order().ToArray();
                var newValues = after.Select(row => Value(row, budget.Metric)).Order().ToArray();
                var oldMedian = Median(oldValues); var newMedian = Median(newValues);
                var oldSpread = Spread(oldValues, oldMedian); var newSpread = Spread(newValues, newMedian);
                var allowed = Math.Max(budget.Absolute, oldMedian * (budget.Percent / 100));
                var decrease = budget.Metric == "requestsPerSecond";
                var change = decrease ? oldMedian - newMedian : newMedian - oldMedian;
                var separation = decrease ? oldValues[0] - newValues[^1] : newValues[0] - oldValues[^1];
                var status = oldSpread > policy.MaximumSpreadPercent || newSpread > policy.MaximumSpreadPercent ? "noise" :
                    change <= allowed ? "passed" : separation > allowed ? "regression" : "noise";
                checks.Add(new(key.Gateway, key.Scenario, budget.Metric, oldMedian, newMedian, allowed, oldSpread, newSpread, status));
            }
        }
        var overall = failures.Count > 0 ? "request-failure" : checks.Any(c => c.Status == "regression") ? "regression" :
            checks.Any(c => c.Status == "noise") ? "noise" : "passed";
        return new(overall, overall switch { "request-failure" => 1, "regression" => 2, "noise" => 3, _ => 0 }, failures.ToArray(), checks.ToArray());
    }

    private static Dictionary<(string Gateway, string Scenario), JsonNode[]> Rows(JsonNode report, int minimum)
    {
        var rows = report["results"]?.AsArray() ?? throw new ArgumentException("Missing results.");
        if (rows.Count is < 1 or > 10000 || rows.Any(r => r is null)) throw new ArgumentException("Invalid result count.");
        var grouped = rows.Select(r => r!).GroupBy(r => (Gateway: r["gateway"]?.GetValue<string>() ?? throw new ArgumentException("Missing gateway."), Scenario: r["scenario"]?.GetValue<string>() ?? throw new ArgumentException("Missing scenario.")))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in grouped.Values)
        {
            if (group.Length < minimum || group.Length != report["settings"]!["repetitions"]?.GetValue<int>() ||
                !group.Select(r => (r["repetition"]?.GetValue<int>() ?? 0)).Order().SequenceEqual(Enumerable.Range(1, group.Length)))
                throw new ArgumentException("Insufficient, missing, or duplicate repetitions.");
            var hash = group[0]["configurationHash"]?.GetValue<string>();
            if (hash is not { Length: 64 } || group.Any(r => r["configurationHash"]?.GetValue<string>() != hash))
                throw new ArgumentException("Missing or changing configuration identity.");
        }
        return grouped;
    }
    private static JsonObject HostHashes(JsonNode report)
    {
        var result = new JsonObject();
        foreach (var host in report["hosts"]!.AsArray())
        {
            var name = host?["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing host name.");
            var hash = host?["manifest"]?["configurationHash"]?.GetValue<string>();
            if (hash is not { Length: 64 } || result.ContainsKey(name)) throw new ArgumentException("Invalid host identity.");
            result.Add(name, hash);
        }
        if (result.Count < 2) throw new ArgumentException("Managed backend and gateway identities are required.");
        return result;
    }
    private static double Value(JsonNode row, string metric)
    {
        if (metric == "firstByteP95Ms" && Number(row, "firstByteSamples") <= 0) throw new ArgumentException("First-byte samples are unavailable.");
        if (metric != "droppedPercent") return Number(row, metric);
        var arrivals = row["arrivals"] ?? throw new ArgumentException("Arrival measurements are unavailable.");
        var offered = Number(arrivals, "offered");
        return offered == 0 ? 0 : (Number(arrivals, "dropped") + Number(arrivals, "cancelledBeforeStart")) * 100 / offered;
    }
    private static double Number(JsonNode row, string field)
    {
        var value = row[field]?.Deserialize<double>() ?? throw new ArgumentException("Missing metric: " + field);
        if (!double.IsFinite(value) || value < 0) throw new ArgumentException("Invalid metric: " + field);
        return value;
    }
    private static double Median(double[] values) => values[(values.Length - 1) / 2] / 2 + values[values.Length / 2] / 2;
    private static double Spread(double[] values, double median) => median == 0 ? values[^1] == 0 ? 0 : double.MaxValue : Math.Min(double.MaxValue, (values[^1] - values[0]) / median * 100);
    private static void RequireEqual(JsonNode? left, JsonNode? right, string field)
    {
        if (!JsonNode.DeepEquals(left, right)) throw new ArgumentException("Incompatible comparison field: " + field);
    }
}
