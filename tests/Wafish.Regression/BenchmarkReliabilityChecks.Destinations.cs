using System.Net.Http.Json;
using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task DestinationOptionsAsync()
    {
        using var transport = new BenchmarkTransport("http1");
        var defaults = BenchmarkDestinations.Read([], transport);
        Check(defaults.Addresses.Single().Port == 5300 && defaults.LoadBalancingPolicy == "PowerOfTwoChoices", "Single-backend defaults changed.");
        Check(BenchmarkDestinations.Id(0) == "backend" && BenchmarkDestinations.Id(7) == "backend-8", "Destination identity is unstable.");
        var maximum = string.Join(',', Enumerable.Range(1, 8).Select(i => "http://127.0.0.1:" + (5300 + i)));
        foreach (var policy in new[] { "RoundRobin", "Random", "LeastRequests", "PowerOfTwoChoices", "FirstAlphabetical" })
            Check(BenchmarkDestinations.Read(["--backend-urls", maximum, "--load-balancing-policy", policy], transport).Addresses.Length == 8, "Bounded destinations rejected.");
        foreach (var invalid in new[]
        {
            new[] { "--backend-urls=" }, new[] { "--backend-urls=http://127.0.0.1:5300," },
            new[] { "--backend-urls=" + maximum + ",http://127.0.0.1:5310" },
            new[] { "--backend-urls=" + new string('x', 4097) },
            new[] { "--backend-urls=http://127.0.0.1:5300,http://localhost:5300" },
            new[] { "--backend-urls=http://127.0.0.1:0" }, new[] { "--backend-urls=http://example.com:5300" },
            new[] { "--backend-urls=https://127.0.0.1:5300" }, new[] { "--backend-urls=http://127.0.0.1:5300/path" },
            new[] { "--backend-url=http://127.0.0.1:5300", "--backend-urls=http://127.0.0.1:5301" },
            new[] { "--load-balancing-policy=roundrobin" }
        })
        {
            try { BenchmarkDestinations.Read(invalid, transport); throw new Exception("Invalid destination configuration accepted."); }
            catch (ArgumentException) { }
        }
        foreach (var args in new[]
        {
            new[] { "--suite=first", "--backend-urls=http://127.0.0.1:12341,http://127.0.0.1:12342" },
            new[] { "--external-url=http://127.0.0.1:12340", "--backend-urls=http://127.0.0.1:12341" },
            new[] { "--backend-urls=http://127.0.0.1:12341,http://127.0.0.1:12342", "--native-url=http://127.0.0.1:12342" }
        })
        {
            await using var runner = await BenchmarkProcess.StartAsync("Runner", args, ready: false);
            Check((await runner.FinishAsync()).ExitCode != 0, "Unsupported or conflicting runner topology was accepted.");
            Check(!Directory.EnumerateDirectories(runner.DirectoryPath, "wafish-perf-*").Any(), "Rejected topology started managed resources.");
        }
    }

    private static async Task DestinationPoliciesAsync()
    {
        await using var first = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        await using var second = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        using var client = new HttpClient();
        var origins = first.Address + "," + second.Address;
        foreach (var policy in new[] { "RoundRobin", "FirstAlphabetical" })
        foreach (var mode in new[] { "native", "host-native", "core", "full" })
        {
            foreach (var address in new[] { first.Address, second.Address })
            {
                using var reset = await client.PostAsync(new Uri(address, "/api/perf/control/reset"), null);
                reset.EnsureSuccessStatusCode();
            }
            var args = new List<string> { "--listen-url=" + FreeAddress(), "--backend-urls=" + origins, "--load-balancing-policy=" + policy };
            if (mode != "native") args.Add("--mode=" + mode);
            await using var proxy = await BenchmarkProcess.StartAsync(mode == "native" ? "NativeYarp" : "EnhancedYarp", args.ToArray());
            var manifest = await client.GetFromJsonAsync<JsonElement>(new Uri(proxy.Address, "/benchmark/manifest"));
            var cluster = manifest.GetProperty("configuration").GetProperty("clusters")[0];
            Check(cluster.GetProperty("loadBalancingPolicy").GetString() == policy
                && cluster.GetProperty("destinations").EnumerateObject().Count() == 2, "Proxy manifest lost the requested topology.");
            for (var i = 0; i < 8; i++)
                Check(await client.GetStringAsync(new Uri(proxy.Address, "/api/perf/plain")) == "OK", "Multi-destination forwarding changed payload.");
            var counts = new List<long>();
            foreach (var address in new[] { first.Address, second.Address })
            {
                long total = 0;
                await UntilAsync(async () =>
                {
                    var body = await client.GetFromJsonAsync<JsonElement>(new Uri(address, "/api/perf/control/counters"));
                    total = body.GetProperty("total").GetInt64();
                    return body.GetProperty("active").GetInt64() == 0;
                });
                counts.Add(total);
            }
            Check(policy == "RoundRobin" ? counts.SequenceEqual([4L, 4L]) : counts.SequenceEqual([8L, 0L]), "Effective policy did not match request distribution: " + mode);
        }
    }
}
