using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task MultipleDestinationsAsync()
    {
        using var transport = new BenchmarkTransport("http1");
        foreach (var args in new[] {
            new[] { "--backend-urls=http://127.0.0.1:1,http://localhost:1" },
            new[] { "--backend-urls=http://127.0.0.1:1,https://127.0.0.1:2" },
            new[] { "--backend-urls=http://127.0.0.1:1,https://example.com" },
            new[] { "--backend-urls=" + string.Join(',', Enumerable.Range(1, 9).Select(i => "http://127.0.0.1:" + i)) },
            new[] { "--backend-urls=http://127.0.0.1:1", "--backend-url=http://127.0.0.1:2" },
            new[] { "--load-balancing=invalid" } })
        {
            try { BenchmarkDestinations.Read(args, transport); throw new Exception("Invalid destination options accepted."); }
            catch (ArgumentException) { }
        }
        Check(BenchmarkDestinations.Read([], transport).Addresses.Length == 1, "Default destination count changed.");
        foreach (var policy in new[] { "RoundRobin", "FirstAlphabetical" })
        {
            var ports = new HashSet<int>(); while (ports.Count < 4) ports.Add(FreeAddress().Port);
            var addresses = ports.Select(port => "http://127.0.0.1:" + port).ToArray();
            await using var runner = await BenchmarkProcess.StartAsync("Runner", [
                "--backend-urls=" + string.Join(',', addresses.Take(2)), "--native-url=" + addresses[2],
                "--enhanced-url=" + addresses[3], "--modes=native,host-native,full", "--load-balancing=" + policy,
                "--transport=" + (policy == "RoundRobin" ? "http1" : "h2c"),
                "--duration=1", "--warmup=0", "--concurrency=1", "--repetitions=1"], ready: false);
            var result = await runner.FinishAsync();
            Check(result.ExitCode == 0, "Multiple-destination comparison failed: " + result.Output);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.json").Single()));
            var root = report.RootElement;
            Check(root.GetProperty("destinations").GetProperty("count").GetInt32() == 2 &&
                root.GetProperty("destinations").GetProperty("policy").GetString() == policy, "Report lost destination settings.");
            var rows = root.GetProperty("results").EnumerateArray().ToArray();
            Check(rows.Length == 9, "Destination comparison omitted a host or workload.");
            foreach (var row in rows)
            {
                var counts = row.GetProperty("destinationResponses");
                var first = counts.GetProperty("backend-1").GetInt64();
                var second = counts.GetProperty("backend-2").GetInt64();
                Check(counts.GetProperty("__unknown__").GetInt64() == 0 && first + second == row.GetProperty("completedResponses").GetInt64(), "Distribution lost responses.");
                Check(first > 0 && (policy == "RoundRobin" ? Math.Abs(first - second) <= 1 : second == 0), "Destination policy was not applied equally.");
            }
        }
    }
}
