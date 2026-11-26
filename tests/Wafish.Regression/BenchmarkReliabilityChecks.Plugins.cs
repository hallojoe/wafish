using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task PluginCombinationsAsync()
    {
        foreach (var invalid in new[] { "", "unknown", "waf+waf", "none+waf", "waf;waf", "waf+compression;compression+waf",
            string.Join(';', Enumerable.Repeat("none", 9)), new string('x', 2049), "waf+compression+rate-limit+traffic-metrics+request-retry+response-cache+waf" })
        {
            try { BenchmarkPluginMatrix.Parse(invalid); throw new Exception("Invalid plugin matrix accepted."); }
            catch (ArgumentException) { }
        }
        var matrix = BenchmarkPluginMatrix.Parse("none;waf+rate-limit;response-cache+compression+traffic-metrics+request-retry");
        Check(matrix.Length == 3 && matrix[0].Plugins.Length == 0 && matrix[1].Name == "rate-limit+waf", "Canonical plugin selection changed.");
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        using var backendClient = new HttpClient { BaseAddress = backend.Address };
        await using (var host = await BenchmarkProcess.StartAsync("EnhancedYarp", ["--listen-url=" + FreeAddress(),
            "--backend-url=" + backend.Address, "--mode=plugins", "--plugins=response-cache+compression"]))
        {
            using var client = new HttpClient { BaseAddress = host.Address };
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            string? previous = null;
            for (var index = 0; index < 2; index++)
            {
                using var response = await client.GetAsync("/api/perf/cacheable-json");
                Check(response.IsSuccessStatusCode && response.Content.Headers.ContentEncoding.Contains("gzip"), "Selected compression did not execute.");
                await using var body = await response.Content.ReadAsStreamAsync();
                await using var decoded = new GZipStream(body, CompressionMode.Decompress);
                using var reader = new StreamReader(decoded, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                if (previous is not null) Check(text == previous, "Cache and compression changed the response.");
                previous = text;
            }
            Check((await Counters(backendClient)).GetProperty("total").GetInt64() == 1, "Selected response cache did not execute.");
        }
        await using (var host = await BenchmarkProcess.StartAsync("EnhancedYarp", ["--listen-url=" + FreeAddress(),
            "--backend-url=" + backend.Address, "--mode=plugins", "--plugins=waf+rate-limit+traffic-metrics+request-retry"]))
        {
            using var client = new HttpClient { BaseAddress = host.Address };
            using var rejected = await client.GetAsync("/api/perf/plain?q=%27%20OR%201%3D1--");
            Check(rejected.StatusCode == HttpStatusCode.Forbidden, "Selected WAF did not execute.");
            using var success = await client.GetAsync("/api/perf/plain");
            Check(success.IsSuccessStatusCode, "Combined normal request failed.");
        }
        var ports = new HashSet<int>(); while (ports.Count < 3) ports.Add(FreeAddress().Port);
        var addresses = ports.Select(port => "http://127.0.0.1:" + port).ToArray();
        await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--backend-url=" + addresses[0],
            "--native-url=" + addresses[1], "--enhanced-url=" + addresses[2], "--suite=plugins",
            "--plugin-sets=" + string.Join(';', matrix.Select(set => set.Name)), "--duration=1", "--warmup=0",
            "--concurrency=1", "--repetitions=1"], ready: false);
        var result = await runner.FinishAsync();
        Check(result.ExitCode == 0, "Plugin comparison failed: " + result.Output);
        var path = Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.json").Single();
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Check(report.RootElement.GetProperty("pluginSets").GetArrayLength() == 3 && report.RootElement.GetProperty("acceptEncoding").GetString() == "gzip", "Report omitted selected matrix or request encoding.");
        var hosts = report.RootElement.GetProperty("hosts").EnumerateArray().Where(host => host.GetProperty("name").GetString()!.StartsWith("Plugins:")).ToArray();
        Check(hosts.Length == 3, "Matrix did not create exactly the selected hosts.");
        foreach (var host in hosts)
        {
            var config = host.GetProperty("manifest").GetProperty("configuration");
            var expected = config.GetProperty("pluginSelection").GetProperty("plugins").EnumerateArray().Select(p => p.GetString()).ToHashSet();
            var bindings = config.GetProperty("bindings").EnumerateArray().ToArray();
            Check(expected.SetEquals(bindings.Select(b => b.GetProperty("pluginId").GetString())), "Manifest included missing or extra plugins.");
            Check(bindings.All(b => b.GetProperty("configuration").ValueKind == JsonValueKind.Object && b.GetProperty("order").GetInt32() == b.GetProperty("manifestOrder").GetInt32()), "Effective settings or execution order are missing.");
        }
        var rows = report.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Check(rows.Length == 4 && rows.All(row => row.GetProperty("failures").GetInt64() == 0 && row.GetProperty("configurationHash").GetString()!.Length == 64), "Matrix measurements lost success or attribution.");
        Check((await File.ReadAllTextAsync(Path.ChangeExtension(path, ".md"))).Contains("Plugin sets:"), "Markdown omitted the selected combinations.");
    }
}
