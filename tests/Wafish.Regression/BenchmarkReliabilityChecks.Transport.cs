using System.Net;
using System.Net.Security;
using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task MatchedTransportsAsync()
    {
        try { using var invalid = new BenchmarkTransport("http3"); throw new Exception("Unknown transport accepted."); }
        catch (ArgumentException) { }
        using var https = new BenchmarkTransport("https2");
        Check(https.Address("https://127.0.0.1:1234", "test").Port == 1234, "TLS loopback address rejected.");
        try { https.Address("http://127.0.0.1:1234", "test"); throw new Exception("Mismatched scheme accepted."); }
        catch (ArgumentException) { }
        try { _ = https.Certificate; throw new Exception("TLS host accepted missing certificate."); }
        catch (ArgumentException) { }
        var directory = Path.Combine(Path.GetTempPath(), "wafish-tls-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "one.pfx"); var second = Path.Combine(directory, "two.pfx");
            BenchmarkTransport.CreateCertificate(first); BenchmarkTransport.CreateCertificate(second);
            using var pinned = new BenchmarkTransport("https2", first);
            using var other = new BenchmarkTransport("https2", second);
            using var handler = new SocketsHttpHandler(); pinned.ConfigureHandler(handler);
            var validate = handler.SslOptions.RemoteCertificateValidationCallback!;
            Check(validate(handler, pinned.Certificate, null, SslPolicyErrors.RemoteCertificateChainErrors), "Pinned self-signed certificate rejected.");
            Check(!validate(handler, other.Certificate, null, SslPolicyErrors.None), "Different certificate bypassed the pin.");
            Check(!validate(handler, pinned.Certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch), "Certificate name mismatch bypassed validation.");
            Check(!validate(handler, null, null, SslPolicyErrors.RemoteCertificateNotAvailable), "Missing certificate accepted.");
            using var wrongResponse = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version11 };
            try { pinned.VerifyResponse(wrongResponse); throw new Exception("Protocol downgrade accepted."); }
            catch (InvalidOperationException) { }
        }
        finally { Directory.Delete(directory, true); }

        foreach (var mode in new[] { "http1", "h2c", "https1", "https2" })
        {
            using var transport = new BenchmarkTransport(mode);
            var ports = new HashSet<int>(); while (ports.Count < 4) ports.Add(FreeAddress().Port);
            var addresses = ports.Select(port => $"{transport.Scheme}://127.0.0.1:{port}").ToArray();
            await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--transport=" + mode,
                "--backend-urls=" + addresses[0] + "," + addresses[1], "--native-url=" + addresses[2], "--enhanced-url=" + addresses[3], "--load-balancing-policy=RoundRobin",
                "--modes=native,full", "--duration=1", "--warmup=0", "--concurrency=2", "--repetitions=1"], ready: false);
            var result = await runner.FinishAsync();
            Check(result.ExitCode == 0, mode + " comparison failed: " + result.Output);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.json").Single()));
            Check(report.RootElement.GetProperty("transport").GetProperty("mode").GetString() == mode, "Report lost selected transport.");
            var rows = report.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Check(rows.Length == 6 && rows.All(row => row.GetProperty("failures").GetInt64() == 0 && row.GetProperty("requests").GetInt64() > 0), "Matched hosts lost successful measurements.");
            Check(report.RootElement.GetProperty("destinations").GetProperty("count").GetInt32() == 2
                && report.RootElement.GetProperty("destinations").GetProperty("policy").GetString() == "RoundRobin", "Report lost destination count or policy.");
            foreach (var row in rows)
            {
                var counts = row.GetProperty("destinationRequests").EnumerateObject().Select(property => property.Value.GetInt64()).ToArray();
                Check(counts.Length == 2 && counts.All(count => count > 0) && counts.Sum() == row.GetProperty("requests").GetInt64()
                    && Math.Abs(counts[0] - counts[1]) <= 1, "Measured backend distribution included warmup, lost requests, or used the wrong policy.");
            }
            Check((await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.csv").Single())).Contains("DestinationRequests"), "CSV omitted destination distribution.");
            Check((await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.md").Single())).Contains("Destinations: 2"), "Markdown omitted destination topology.");
            Check(!Directory.EnumerateDirectories(runner.DirectoryPath, "wafish-perf-*").Any(), "Run retained private certificate or databases.");
        }
    }
}
