using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task PacedStreamsAsync()
    {
        foreach (var (kind, chunks, bytes, interval) in new[] { ("bad", 2, 100, 100), ("chunks", 0, 1, 1),
            ("chunks", 257, 1, 1), ("events", 256, 65536, 1), ("chunks", 20, 100, 1000), ("chunks", 1, 1, -1), ("chunks", 1, 65537, 1) })
        {
            try { _ = new BenchmarkStreamWorkload(kind, chunks, bytes, interval); throw new Exception("Invalid stream bounds accepted."); }
            catch (ArgumentException) { }
        }
        var valid = new BenchmarkStreamWorkload("events", 2, 8, 0);
        Check(BenchmarkStreamWorkload.ParsePath(valid.Path) == valid && valid.Matches(valid.CreateChunk().Concat(valid.CreateChunk()).ToArray()), "Event framing or roundtrip failed.");
        Check(!valid.Matches([]) && !valid.Matches(new byte[valid.TotalBytes]), "Invalid stream payload accepted.");
        using (var empty = new MemoryStream())
            Check((await BenchmarkBodyMeasurement.ReadAsync(empty, Stopwatch.GetTimestamp(), default)).Reads == 0, "Empty response invented a first byte.");
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress()]);
        using var client = new HttpClient { BaseAddress = backend.Address };
        foreach (var kind in new[] { "chunks", "events" })
        {
            await BenchmarkPreflight.VerifyAsync(backend.Address, HttpMethod.Get, new BenchmarkStreamWorkload(kind, 3, 128, 100).Path);
            using var response = await client.GetAsync(new BenchmarkStreamWorkload(kind, 5, 128, 800).Path, HttpCompletionOption.ResponseHeadersRead);
            Check(response.Content.Headers.ContentType!.MediaType == (kind == "events" ? "text/event-stream" : "text/plain"), "Stream media type changed.");
            Check(response.Content.Headers.ContentLength is null, "Paced response advertised a complete buffered body.");
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[128];
            Check(await stream.ReadAsync(buffer) > 0 && (await Counters(client)).GetProperty("active").GetInt64() == 1, "First bytes arrived only after production completed.");
            response.Dispose();
            await UntilAsync(async () => (await Counters(client)).GetProperty("active").GetInt64() == 0);
        }
        Check((await Counters(client)).GetProperty("cancelled").GetInt64() >= 2, "Disconnected stream producers kept working.");
        using (var bad = await client.GetAsync("/api/perf/stream/chunks/256/65536/1000"))
            Check(bad.StatusCode == HttpStatusCode.BadRequest, "Oversized stream accepted.");
        foreach (var kind in new[] { "chunks", "events" })
        {
            var ports = new HashSet<int>(); while (ports.Count < 3) ports.Add(FreeAddress().Port);
            var addresses = ports.Select(port => "http://127.0.0.1:" + port).ToArray();
            await using var runner = await BenchmarkProcess.StartAsync("Runner", ["--backend-url=" + addresses[0],
                "--native-url=" + addresses[1], "--enhanced-url=" + addresses[2], "--modes=native,full",
                "--workload=" + kind, "--stream-chunks=4", "--stream-bytes=128", "--stream-interval=100",
                "--duration=1", "--warmup=0", "--concurrency=1", "--repetitions=1"], ready: false);
            var result = await runner.FinishAsync();
            Check(result.ExitCode == 0, "Streaming comparison failed: " + result.Output);
            var path = Directory.GetFiles(Path.Combine(runner.DirectoryPath, "results"), "*.json").Single();
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Check(report.RootElement.GetProperty("streaming").GetProperty("kind").GetString() == kind, "Report omitted stream configuration.");
            var rows = report.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Check(rows.Length == 2, "Streaming matrix contains unrelated workloads.");
            foreach (var row in rows)
            {
                var responses = row.GetProperty("completedResponses").GetInt64();
                Check(responses > 0 && row.GetProperty("firstByteSamples").GetInt64() == responses, "First-byte samples lost complete streams.");
                Check(row.GetProperty("firstByteP95Ms").GetDouble() < row.GetProperty("p95Ms").GetDouble(), "Report cannot distinguish early delivery from completion.");
                Check(row.GetProperty("responseBytes").GetInt64() == responses * new BenchmarkStreamWorkload(kind, 4, 128, 100).TotalBytes, "Response byte count changed.");
                Check(row.GetProperty("readGapP95Ms").GetDouble() > 0, "Read-gap observations are missing.");
            }
            Check((await File.ReadAllTextAsync(Path.ChangeExtension(path, ".csv"))).Contains("FirstByteP95Ms"), "CSV omitted delivery measurements.");
        }
    }
}
