using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Benchmarks: fixed-memory histograms bound percentile error without losing samples", HistogramAsync);
        await run("Benchmarks: explicit addresses reject non-loopback and ambiguous origins", OptionsAsync);
        await run("Benchmarks: normalized manifests hash composition and redact nested secrets", ManifestAsync);
        await run("Benchmarks: workload preflight rejects wrong status, payload, size, and stalled reads", PreflightAsync);
        await run("Benchmarks: isolated backend cancellation drains work and identifies payload allocation", BackendAsync);
        await run("Benchmarks: opt-in transport faults truncate bodies and cancel stalled reads", TransportFaultsAsync);
        await run("Benchmarks: native forwarding preflight detects backend failure on isolated addresses", NativeAsync);
        await run("Benchmarks: matched transport modes enforce protocols and pin local TLS certificates", MatchedTransportsAsync);
        await run("Benchmarks: scheduled arrivals expose overload with bounded queues and separate latency", ScheduledArrivalsAsync);
        await run("Benchmarks: managed comparisons export effective manifests and clean only owned data", RunnerAsync);
    }
    private static async Task HistogramAsync()
    {
        var histogram = new BoundedLatencyHistogram();
        Check(histogram.Percentile(.99) == 0, "Empty histogram did not report zero.");
        var random = new Random(91);
        var exact = Enumerable.Range(0, 250_000).Select(index => index % 3 == 0 ? random.NextDouble() * .001 : Math.Pow(10, random.NextDouble() * 10 - 3)).ToArray();
        var retained = histogram.RetainedBytes;
        foreach (var value in exact) histogram.Record(value);
        Array.Sort(exact);
        foreach (var percentile in new[] { 0d, .01, .5, .95, .99, 1 })
        {
            var actual = exact[(int)Math.Clamp(Math.Ceiling(percentile * exact.Length) - 1, 0, exact.Length - 1)];
            var estimate = histogram.Percentile(percentile);
            Check(estimate + 1e-10 >= actual && estimate <= Math.Max(.001, actual * 1.010000001), "Histogram exceeded its stated percentile error.");
        }
        Check(histogram.Count == exact.Length && retained == histogram.RetainedBytes && retained < 32768, "Histogram retained unbounded samples or lost observations.");
        var parallel = new BoundedLatencyHistogram();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { for (var index = 0; index < 100_000; index++) parallel.Record(5); })));
        Check(parallel.Count == 800_000 && parallel.Percentile(.99) is >= 5 and <= 5.05, "Concurrent histogram writes lost samples.");
        histogram.Record(0); histogram.Record(BoundedLatencyHistogram.MaximumMilliseconds);
        Check(histogram.Percentile(1) == BoundedLatencyHistogram.MaximumMilliseconds, "Maximum latency was silently clamped to a smaller bucket.");
        foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity, BoundedLatencyHistogram.MaximumMilliseconds + 1 })
        {
            try { histogram.Record(invalid); throw new Exception("Invalid latency accepted."); } catch (ArgumentOutOfRangeException) { }
        }
        foreach (var invalid in new[] { -1d, double.NaN, 2d })
        {
            try { histogram.Percentile(invalid); throw new Exception("Invalid percentile accepted."); } catch (ArgumentOutOfRangeException) { }
        }
    }
    private static Task OptionsAsync()
    {
        Check(BenchmarkOptions.Read([], "--name", "default") == "default", "Default argument changed.");
        Check(BenchmarkOptions.Read(["--name=value"], "--name", "default") == "value" && BenchmarkOptions.Read(["--name", "value"], "--name", "default") == "value", "Argument forms differ.");
        foreach (var args in new[] { new[] { "--name" }, new[] { "--name", "--other" } })
        { try { BenchmarkOptions.Read(args, "--name", "default"); throw new Exception("Missing option value accepted."); } catch (ArgumentException) { } }
        foreach (var address in new[] { "http://127.0.0.1:12345", "http://[::1]:12345/", "http://localhost:12345" })
            Check(BenchmarkOptions.LoopbackAddress(address, "test").Port == 12345, "Loopback address changed.");
        foreach (var address in new[] { "http://0.0.0.0:12345", "http://example.com", "https://127.0.0.1", "http://a:b@127.0.0.1", "http://127.0.0.1/path", "http://127.0.0.1?q=1", "http://127.0.0.1/#secret", "invalid", "http://127.0.0.1:65536" })
        { try { BenchmarkOptions.LoopbackAddress(address, "test"); throw new Exception("Invalid origin accepted."); } catch (ArgumentException) { } }
        return Task.CompletedTask;
    }
    private static Task ManifestAsync()
    {
        var first = BenchmarkManifest.Create(new Dictionary<string, object?> { ["mode"] = "test", ["nested"] = new { Password = "secret-one", tokenLimit = 25, children = new[] { new { ApiKey = "key-one", enabled = true } } } });
        var second = BenchmarkManifest.Create(new Dictionary<string, object?> { ["nested"] = new { Password = "secret-two", tokenLimit = 25, children = new[] { new { ApiKey = "key-two", enabled = true } } }, ["mode"] = "test" });
        Check(first.ConfigurationHash == second.ConfigurationHash && first.ConfigurationHash.Length == 64, "Manifest order or secret values changed the public composition hash.");
        var json = JsonSerializer.Serialize(first);
        Check(!json.Contains("secret-one") && !json.Contains("key-one") && json.Contains("25") && json.Contains("redacted"), "Manifest leaked secrets or redacted nonsecret quota configuration.");
        Check(BenchmarkManifest.Create(new { mode = "changed" }).ConfigurationHash != first.ConfigurationHash, "Different configurations shared a hash.");
        Check(BenchmarkManifest.Create(new { value = (object?)null }).Configuration is not null, "Null settings broke manifest normalization.");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
