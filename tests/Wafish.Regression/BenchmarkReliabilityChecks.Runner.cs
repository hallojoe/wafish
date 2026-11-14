using System.Net;
using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task NativeAsync()
    {
        var backendAddress = FreeAddress();
        await using (var unavailable = await BenchmarkProcess.StartAsync("NativeYarp", ["--listen-url=" + FreeAddress(), "--backend-url=" + backendAddress]))
        {
            try { await BenchmarkPreflight.VerifyAsync(unavailable.Address, HttpMethod.Get, "/api/perf/plain"); throw new Exception("Healthy native process hid an unreachable backend."); }
            catch (InvalidOperationException) { }
        }
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + backendAddress]);
        await using var native = await BenchmarkProcess.StartAsync("NativeYarp", ["--listen-url=" + FreeAddress(), "--backend-url=" + backend.Address]);
        await BenchmarkPreflight.VerifyAsync(native.Address, HttpMethod.Get, "/api/perf/plain");
        using var client = new HttpClient { BaseAddress = native.Address };
        using var manifest = JsonDocument.Parse(await client.GetStringAsync("/benchmark/manifest"));
        var configuration = manifest.RootElement.GetProperty("configuration");
        Check(configuration.GetProperty("clusters")[0].GetProperty("destinations").GetProperty("backend").GetProperty("address").GetString() == backend.Address.ToString(), "Baseline manifest lost its effective upstream.");
        Check(configuration.GetProperty("proxyVersion").GetString() is { Length: > 0 } && configuration.GetProperty("upstreamTransport").GetString()!.Contains("1.1 exact"), "Baseline transport metadata is incomplete.");
        await using var enhanced = await BenchmarkProcess.StartAsync("EnhancedYarp", ["--listen-url=" + FreeAddress(), "--backend-url=" + backend.Address, "--mode=log-meta"]);
        using var enhancedClient = new HttpClient { BaseAddress = enhanced.Address };
        using var effective = JsonDocument.Parse(await enhancedClient.GetStringAsync("/benchmark/manifest"));
        Check(effective.RootElement.GetProperty("configuration").GetProperty("bindings")[0].GetProperty("pluginId").GetString() == "proxy-log", "Benchmark manifest omitted the effective binding.");
        var resources = effective.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        Check(resources.Length > 0 && resources.All(resource => resource.GetProperty("running").GetBoolean()), "Benchmark readiness preceded resource startup.");
    }
    private static async Task RunnerAsync()
    {
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(FreeAddress().Port);
        var addresses = ports.Select(port => "http://127.0.0.1:" + port).ToArray();
        var sentinel = Path.Combine(BenchmarkProcess.Root, "performance-sentinel-" + Guid.NewGuid().ToString("N") + ".db");
        await File.WriteAllTextAsync(sentinel, "unowned benchmark state");
        try
        {
            await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--backend-url=" + addresses[0], "--native-url=" + addresses[1], "--enhanced-url=" + addresses[2], "--duration=1", "--warmup=0", "--concurrency=2", "--repetitions=1"], ready: false);
            var result = await runner.FinishAsync();
            Check(result.ExitCode == 0, "Managed benchmark failed: " + result.Output);
            Check(await File.ReadAllTextAsync(sentinel) == "unowned benchmark state", "Managed cleanup deleted unrelated files.");
            Check(!Directory.EnumerateDirectories(runner.DirectoryPath, "wafish-perf-*").Any(), "Managed run retained its private databases.");
            var reports = Path.Combine(runner.DirectoryPath, "results");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(reports, "*.json").Single()));
            var root = report.RootElement;
            var environment = root.GetProperty("environment");
            Check(environment.GetProperty("logicalProcessors").GetInt32() > 0 && environment.GetProperty("runtime").GetString()!.Length > 0 && environment.GetProperty("sourceRevision").GetString()!.Length == 40, "Run metadata omitted environment or source revision.");
            Check(root.GetProperty("latency").GetProperty("retainedBytes").GetInt32() < 32768 && root.GetProperty("hosts").GetArrayLength() == 9, "Run metadata lost histogram or host composition.");
            var rows = root.GetProperty("results").EnumerateArray().ToArray();
            Check(rows.Length == 23 && rows.All(row => row.GetProperty("failures").GetInt64() == 0 && row.GetProperty("configurationHash").GetString()!.Length == 64), "Measured rows lost success or configuration attribution.");
            Check(rows.All(row => !row.GetProperty("gateway").GetString()!.Contains("Aneiang")), "Legacy benchmark labels remain.");
            Check((await File.ReadAllTextAsync(Directory.GetFiles(reports, "*.csv").Single())).Contains("ConfigurationHash"), "CSV rows cannot be attributed to configuration.");
        }
        finally { File.Delete(sentinel); }
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        await using (var external = await BenchmarkProcess.StartAsync("Runner", ["--external-url=" + backend.Address, "--path=/api/perf/plain", "--duration=1", "--warmup=1", "--concurrency=2", "--repetitions=1"], ready: false))
        {
            Check((await external.FinishAsync()).ExitCode == 0, "External benchmark failed.");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(external.DirectoryPath, "results"), "*.json").Single()));
            Check(!report.RootElement.GetProperty("settings").GetProperty("processMeasurementsAvailable").GetBoolean(), "External report claimed process measurements.");
            Check(!Directory.EnumerateDirectories(external.DirectoryPath, "wafish-perf-*").Any(), "External run created managed state.");
        }
        await using (var rejected = await BenchmarkProcess.StartAsync("Runner", ["--external-url=" + backend.Address, "--path=/api/perf/status/503", "--duration=1"], ready: false))
        {
            Check((await rejected.FinishAsync()).ExitCode != 0, "Failed preflight allowed measurement.");
            Check(!Directory.GetFiles(Path.Combine(rejected.DirectoryPath, "results"), "*.json").Any(), "Failed preflight emitted a measurement report.");
        }

    }
}
