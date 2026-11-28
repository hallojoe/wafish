using Wafish.Performance;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var root = FindRepositoryRoot();
using var session = await BenchmarkSession.CreateAsync(args, root);
var resultsDirectory = session.ResultsDirectory;
Directory.CreateDirectory(resultsDirectory);

var durationSeconds = ReadIntArgument(args, "--duration", 10);
var warmupSeconds = ReadIntArgument(args, "--warmup", 3);
var concurrency = ReadIntArgument(args, "--concurrency", 64);
var repetitions = ReadIntArgument(args, "--repetitions", 3);
var suite = ReadStringArgument(args, "--suite", "baseline");
var soakSeconds = ReadIntArgument(args, "--soak-duration", 30);
var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
var results = new List<BenchmarkResult>();
var processes = new List<Process>();
if (durationSeconds <= 0 || warmupSeconds < 0 || concurrency is <= 0 or > 4096 || repetitions is <= 0 or > 100 || soakSeconds <= 0)
    throw new ArgumentException("Duration and soak must be positive; warmup nonnegative; concurrency 1..4096; repetitions 1..100.");
if (durationSeconds > 3600 || warmupSeconds > 3600 || soakSeconds > 3600) throw new ArgumentException("Each phase is limited to one hour.");
if (suite == "first" && session.Streaming is not null) throw new ArgumentException("Streaming workloads require the baseline suite.");
if (suite == "first" && session.ArrivalRate != 0) throw new ArgumentException("Scheduled arrivals require the baseline suite or external mode.");
if (suite is not ("baseline" or "first" or "plugins")) throw new ArgumentException("Unknown benchmark suite.");

var externalUrl = ReadStringArgument(args, "--external-url", "");
if (!string.IsNullOrWhiteSpace(externalUrl))
{
    if (!Uri.TryCreate(externalUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != session.Transport.Scheme || endpoint.UserInfo.Length != 0)
    {
        throw new ArgumentException("--external-url requires a URL matching --transport, without credentials.");
    }

    if (durationSeconds <= 0 || warmupSeconds < 0 || concurrency <= 0 || repetitions <= 0)
    {
        throw new ArgumentException("Duration, concurrency, and repetitions must be positive. Warmup cannot be negative.");
    }

    var scenario = Scenario.Get("external", ReadStringArgument(args, "--path", "/api/local-service/instance"));
    await BenchmarkPreflight.VerifyAsync(endpoint, scenario.Method, scenario.Path, scenario.Body, knownWorkload: false, transport: session.Transport, acceptGzip: session.AcceptGzip);
    Console.WriteLine($"External endpoint: {endpoint}. The runner does not manage processes or databases.");
    if (warmupSeconds > 0)
    {
        await RunLoadAsync(0, scenario, concurrency, TimeSpan.FromSeconds(warmupSeconds), null, endpoint);
    }

    for (var repetition = 1; repetition <= repetitions; repetition++)
    {
        var result = await RunLoadAsync(0, scenario, concurrency, TimeSpan.FromSeconds(durationSeconds), null, endpoint);
        results.Add(result with { Gateway = "External", Scenario = scenario.Name, Repetition = repetition });
    }
    var summary = BuildSummary(results);
    var report = Path.Combine(resultsDirectory, $"external-{timestamp}.md");
    await File.WriteAllTextAsync(report, BuildMarkdown(summary, durationSeconds, warmupSeconds, concurrency, repetitions) +
        "\nExternal mode does not collect process CPU or memory. Zero values mean unavailable.\n" +
        "Keep telemetry settings identical when comparing hosts. Services remain running after this test.\n");
    await File.WriteAllTextAsync(Path.ChangeExtension(report, ".csv"), BuildCsv(results, summary));
    await session.WriteAsync(Path.ChangeExtension(report, ".json"), results, new { mode = "external", processMeasurementsAvailable = false, durationSeconds, warmupSeconds, concurrency, repetitions, requestTimeoutSeconds = 10, clientTransport = BenchmarkSession.Current.Transport.Description });
    Console.WriteLine($"Report: {report}; failures: {results.Sum(r => r.Failures)}");
    if (results.Any(r => r.Failures > 0))
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (suite.Equals("first", StringComparison.OrdinalIgnoreCase))
{
    await RunFirstBatchAsync(root, resultsDirectory, timestamp, durationSeconds, warmupSeconds, soakSeconds);
    return;
}

try
{
    foreach (var (address, index) in session.Destinations.Addresses.Select((address, index) => (address, index)))
    {
        var name = index == 0 ? "Backend" : "Backend:" + BenchmarkDestinations.Id(index);
        var backend = StartService(name, Path.Combine(root, "tests", "Wafish.Performance", "Backend", "bin", "Release", "net10.0", "Backend.dll"), root, listenAddress: address);
        processes.Add(backend);
        await WaitForHealthyAsync(new Uri(address, "/health").ToString(), backend);
        await session.CaptureHostAsync(name, address);
    }

    var gateways = new[]
    {
        new GatewayCase("Native YARP", session.Native.Port, "NativeYarp", Array.Empty<string>()),
        new GatewayCase("Same host native", session.Enhanced.Port, "EnhancedYarp", ["--mode=host-native"]),
        new GatewayCase("Wafish core", session.Enhanced.Port, "EnhancedYarp", ["--mode=core"]),
        new GatewayCase("Wafish storage", session.Enhanced.Port, "EnhancedYarp", ["--mode=storage"]),
        new GatewayCase("Wafish services", session.Enhanced.Port, "EnhancedYarp", ["--mode=services"]),
        new GatewayCase("Wafish minimal", session.Enhanced.Port, "EnhancedYarp", ["--mode=minimal"]),
        new GatewayCase("Wafish full", session.Enhanced.Port, "EnhancedYarp", ["--mode=full"]),
        new GatewayCase("Wafish WAF", session.Enhanced.Port, "EnhancedYarp", ["--mode=waf"])
    };

    if (suite == "plugins")
        gateways = new[] { new GatewayCase("Native YARP", session.Native.Port, "NativeYarp", []) }
            .Concat(session.PluginSets.Select(set => new GatewayCase("Plugins: " + set.Name, session.Enhanced.Port,
                "EnhancedYarp", ["--mode=plugins", "--plugins=" + set.Name]))).ToArray();

    if (session.Modes is { } selected && selected.Any(mode => !gateways.Any(gateway => (gateway.Arguments.FirstOrDefault() ?? "--mode=native")[7..] == mode)))
        throw new ArgumentException("--modes contains an unknown baseline mode.");
    foreach (var gateway in gateways.Where(gateway => session.Modes is null || session.Modes.Contains((gateway.Arguments.FirstOrDefault() ?? "--mode=native")[7..])))
    {
        var dll = Path.Combine(root, "tests", "Wafish.Performance", gateway.Project, "bin", "Release", "net10.0", gateway.Project + ".dll");
        var process = StartService(gateway.Name, dll, root, gateway.Arguments);
        processes.Add(process);
        await WaitForHealthyAsync(new Uri(BenchmarkSession.Current.AddressForPort(gateway.Port), "/health").ToString(), process);
        await session.CaptureHostAsync(gateway.Name, session.AddressForPort(gateway.Port));

        var scenarios = session.Streaming is { } streamWorkload ? new[] { Scenario.Get(streamWorkload.Kind, streamWorkload.Path) } : suite == "plugins" ? new[] { Scenario.Get("cacheable-json", "/api/perf/cacheable-json") } : gateway.Name == "Wafish WAF"
            ? new[] { Scenario.Get("plain", "/api/perf/plain"), Scenario.Get("json-small", "/api/perf/json-small") }
            : new[]
            {
                Scenario.Get("plain", "/api/perf/plain"),
                Scenario.Get("json-small", "/api/perf/json-small"),
                Scenario.Post("post-1kb", "/api/perf/echo", new string('x', 1024))
            };

        foreach (var scenario in scenarios)
        {
            foreach (var address in session.Destinations.Addresses)
                await BenchmarkPreflight.VerifyAsync(address, scenario.Method, scenario.Path, scenario.Body, transport: session.Transport, acceptGzip: session.AcceptGzip);
            await BenchmarkPreflight.VerifyAsync(session.AddressForPort(gateway.Port), scenario.Method, scenario.Path, scenario.Body, transport: BenchmarkSession.Current.Transport, acceptGzip: BenchmarkSession.Current.AcceptGzip);
            Console.WriteLine($"[{gateway.Name}] {scenario.Name}: warmup {warmupSeconds}s, test {durationSeconds}s x {repetitions}, concurrency {concurrency}");
            await RunLoadAsync(gateway.Port, scenario, concurrency, TimeSpan.FromSeconds(warmupSeconds), null);
            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                var before = await session.ReadDestinationCountsAsync();
                var result = await RunLoadAsync(gateway.Port, scenario, concurrency, TimeSpan.FromSeconds(durationSeconds), process);
                var after = await session.ReadDestinationCountsAsync();
                var distribution = after.ToDictionary(pair => pair.Key, pair => pair.Value - before[pair.Key]);
                if (distribution.Values.Any(count => count < 0)) throw new InvalidOperationException("Backend counters reset during measurement.");
                results.Add(result with { Gateway = gateway.Name, Scenario = scenario.Name, Repetition = repetition, DestinationRequests = distribution });
                Console.WriteLine($"  #{repetition}: {result.RequestsPerSecond:N0} RPS, P95 {result.P95Ms:N2} ms, P99 {result.P99Ms:N2} ms, errors {result.ErrorRatePercent:N3}%");
            }
        }

        StopProcess(process);
        processes.Remove(process);
        await Task.Delay(1000);
    }

    var summary = BuildSummary(results);
    var csvPath = Path.Combine(resultsDirectory, $"performance-{timestamp}.csv");
    var markdownPath = Path.Combine(resultsDirectory, $"performance-{timestamp}.md");
    if (results.Any(r => r.Failures > 0 || r.Requests == 0))
    {
        Environment.ExitCode = 1;
    }

    await File.WriteAllTextAsync(csvPath, BuildCsv(results, summary), Encoding.UTF8);
    await File.WriteAllTextAsync(markdownPath, BuildMarkdown(summary, durationSeconds, warmupSeconds, concurrency, repetitions), Encoding.UTF8);

    Console.WriteLine();
    Console.WriteLine("Median results:");
    foreach (var item in summary)
    {
        Console.WriteLine($"{item.Gateway,-18} {item.Scenario,-12} {item.RequestsPerSecond,10:N0} RPS  P95 {item.P95Ms,7:N2} ms  P99 {item.P99Ms,7:N2} ms  CPU {item.CpuPercent,6:N1}%  Memory {item.WorkingSetMb,7:N1} MB");
    }

    await BenchmarkSession.Current.WriteAsync(Path.ChangeExtension(csvPath, ".json"), results, new { processMeasurementsAvailable = true, durationSeconds, warmupSeconds, concurrency, repetitions, suite, requestTimeoutSeconds = 10, clientTransport = BenchmarkSession.Current.Transport.Description });
    Console.WriteLine($"CSV: {csvPath}");
    Console.WriteLine($"Report: {markdownPath}");
}
finally
{
    foreach (var process in processes.ToArray())
    {
        StopProcess(process);
    }

}

static async Task<BenchmarkResult> RunLoadAsync(int port, Scenario scenario, int concurrency, TimeSpan duration, Process? measuredProcess, Uri? endpoint = null, bool allowPolicyRejections = false)
{
    var session = BenchmarkSession.Current;
    using var handler = new SocketsHttpHandler
    {
        MaxConnectionsPerServer = concurrency,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None
    };
    session.Transport.ConfigureHandler(handler);
    using var client = new HttpClient(handler) { BaseAddress = endpoint ?? session.AddressForPort(port), Timeout = TimeSpan.FromSeconds(10) };
    var serviceLatencies = new BoundedLatencyHistogram();
    var scheduledLatencies = new BoundedLatencyHistogram();
    var firstBytes = new BoundedLatencyHistogram();
    var readGaps = new BoundedLatencyHistogram();
    long responseBytes = 0, bodyReads = 0;
    long completed = 0, responses = 0, failed = 0, policyRejections = 0, transportFailures = 0;
    var startedAt = Stopwatch.GetTimestamp();
    var cpuStart = measuredProcess?.TotalProcessorTime ?? TimeSpan.Zero;

    async Task SendAsync(long due, CancellationToken cancellationToken)
    {
        var requestStart = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(scenario.Method, scenario.Path);
            session.Transport.ConfigureRequest(request);
            if (session.AcceptGzip) request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            if (scenario.Body != null) request.Content = new StringContent(scenario.Body, Encoding.UTF8, "application/json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            session.Transport.VerifyResponse(response);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var delivery = await BenchmarkBodyMeasurement.ReadAsync(body, requestStart, deadline.Token);
            Interlocked.Add(ref responseBytes, delivery.Bytes); Interlocked.Add(ref bodyReads, delivery.Reads);
            if (delivery.Reads > 0) { firstBytes.Record(delivery.FirstByteMs); readGaps.Record(delivery.MaximumReadGapMs); }
            Interlocked.Increment(ref responses);
            if (allowPolicyRejections && response.StatusCode == HttpStatusCode.TooManyRequests)
                Interlocked.Increment(ref policyRejections);
            else if (!response.IsSuccessStatusCode) Interlocked.Increment(ref failed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            Interlocked.Increment(ref transportFailures);
            Interlocked.Increment(ref failed);
        }
        finally
        {
            serviceLatencies.Record(Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
            scheduledLatencies.Record(Stopwatch.GetElapsedTime(due).TotalMilliseconds);
            Interlocked.Increment(ref completed);
        }
    }

    var arrivals = await BenchmarkArrivalSchedule.RunAsync(concurrency, duration, session.ArrivalRate, session.ArrivalQueueCapacity, SendAsync);
    var elapsed = Stopwatch.GetElapsedTime(startedAt);
    measuredProcess?.Refresh();
    var cpuEnd = measuredProcess?.TotalProcessorTime ?? cpuStart;
    var cpuPercent = measuredProcess == null ? 0 : (cpuEnd - cpuStart).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
    var memoryMb = measuredProcess?.WorkingSet64 / 1024d / 1024d ?? 0;

    return new BenchmarkResult("", "", 0, completed / elapsed.TotalSeconds,
        serviceLatencies.Percentile(.5), serviceLatencies.Percentile(.95), serviceLatencies.Percentile(.99),
        completed == 0 ? 100 : failed * 100d / completed,
        cpuPercent, memoryMb, completed, failed, policyRejections, transportFailures)
    {
        ConfigurationHash = session.ConfigurationHash(port), Arrivals = arrivals, CompletedResponses = responses,
        ScheduledP95Ms = scheduledLatencies.Percentile(.95), ScheduledP99Ms = scheduledLatencies.Percentile(.99),
        ResponseBytes = responseBytes, BodyReads = bodyReads, FirstByteSamples = firstBytes.Count,
        FirstByteP50Ms = firstBytes.Percentile(.5), FirstByteP95Ms = firstBytes.Percentile(.95),
        FirstByteP99Ms = firstBytes.Percentile(.99), ReadGapP95Ms = readGaps.Percentile(.95)
    };
}

static async Task RunFirstBatchAsync(string root, string resultsDirectory, string timestamp, int durationSeconds, int warmupSeconds, int soakSeconds)
{
    var records = new List<ExtendedResult>();
    var processes = new List<Process>();
    try
    {
        var backendDll = Path.Combine(root, "tests", "Wafish.Performance", "Backend", "bin", "Release", "net10.0", "Backend.dll");
        var enhancedDll = Path.Combine(root, "tests", "Wafish.Performance", "EnhancedYarp", "bin", "Release", "net10.0", "EnhancedYarp.dll");
        var backend = StartService("Backend", backendDll, root);
        processes.Add(backend);
        await WaitForHealthyAsync(new Uri(BenchmarkSession.Current.Backend, "/health").ToString(), backend);
        await BenchmarkSession.Current.CaptureHostAsync("Backend", BenchmarkSession.Current.Backend);

        foreach (var concurrency in new[] { 1, 16, 64, 128 })
        {
            await RunExtendedCaseAsync("Wafish full", "full", Scenario.Get($"concurrency-{concurrency}", "/api/perf/plain"), concurrency, durationSeconds, warmupSeconds, records, processes, enhancedDll, root);
        }
        foreach (var size in new[] { 1024, 64 * 1024, 1024 * 1024 })
        {
            await RunExtendedCaseAsync("Wafish full", "full", Scenario.Get($"response-{size}", $"/api/perf/payload/{size}"), 32, durationSeconds, warmupSeconds, records, processes, enhancedDll, root);
            await RunExtendedCaseAsync("Wafish full", "full", Scenario.Post($"request-{size}", "/api/perf/echo", new string('x', size)), 32, durationSeconds, warmupSeconds, records, processes, enhancedDll, root);
        }

        foreach (var item in new[]
        {
            (Name: "log-meta", Mode: "log-meta", Scenario: Scenario.Get("plain", "/api/perf/plain")),
            (Name: "log-request", Mode: "log-request", Scenario: Scenario.Post("post-64kb", "/api/perf/echo", new string('x', 64 * 1024))),
            (Name: "log-response", Mode: "log-response", Scenario: Scenario.Get("response-64kb", "/api/perf/payload/65536")),
            (Name: "log-sqlite", Mode: "log-sqlite", Scenario: Scenario.Post("post-64kb", "/api/perf/echo", new string('x', 64 * 1024))),
            (Name: "waf-normal", Mode: "waf", Scenario: Scenario.Get("normal", "/api/perf/plain"))
        })
        {
            await RunExtendedCaseAsync(item.Name, item.Mode, item.Scenario, 32, durationSeconds, warmupSeconds, records, processes, enhancedDll, root);
        }

        await RunFunctionalCaseAsync("waf-attack", "waf-attack", "/api/perf/plain?q=%27%20OR%201%3D1--", HttpStatusCode.Forbidden, 0, records, processes, enhancedDll, root);
        foreach (var mode in new[] { "rate-fixed", "rate-sliding", "rate-token", "rate-concurrency" })
        {
            await RunExtendedCaseAsync(mode, mode, Scenario.Get("plain", "/api/perf/plain"), 64, durationSeconds, warmupSeconds, records, processes, enhancedDll, root, allowNonSuccess: true);
        }
        await RunFunctionalCaseAsync("retry", "retry", "/api/perf/flaky/2", HttpStatusCode.OK, 3, records, processes, enhancedDll, root);
        await RunCircuitCaseAsync(records, processes, enhancedDll, root);
        await RunExtendedCaseAsync("soak", "full", Scenario.Get($"plain-{soakSeconds}s", "/api/perf/plain"), 64, soakSeconds, warmupSeconds, records, processes, enhancedDll, root);

        var csvPath = Path.Combine(resultsDirectory, $"performance-first-{timestamp}.csv");
        var markdownPath = Path.Combine(resultsDirectory, $"performance-first-{timestamp}.md");
        if (records.Any(r => !r.Passed))
        {
            Environment.ExitCode = 1;
        }

        await File.WriteAllTextAsync(csvPath, BuildExtendedCsv(records), Encoding.UTF8);
        await File.WriteAllTextAsync(markdownPath, BuildExtendedMarkdown(records, durationSeconds, soakSeconds), Encoding.UTF8);
        await BenchmarkSession.Current.WriteAsync(Path.ChangeExtension(csvPath, ".json"), records, new { processMeasurementsAvailable = true, durationSeconds, warmupSeconds, soakSeconds, suite = "first", requestTimeoutSeconds = 10, clientTransport = BenchmarkSession.Current.Transport.Description });
        Console.WriteLine($"CSV: {csvPath}");
        Console.WriteLine($"Report: {markdownPath}");
    }
    finally
    {
        foreach (var process in processes.ToArray())
        {
            StopProcess(process);
        }

    }
}

static async Task RunExtendedCaseAsync(string group, string mode, Scenario scenario, int concurrency, int durationSeconds, int warmupSeconds, List<ExtendedResult> records, List<Process> processes, string dll, string root, bool allowNonSuccess = false)
{
    var process = StartService(group, dll, root, [$"--mode={mode}"]);
    processes.Add(process);
    await WaitForHealthyAsync(new Uri(BenchmarkSession.Current.Enhanced, "/health").ToString(), process);
    await BenchmarkSession.Current.CaptureHostAsync("EnhancedYarp", BenchmarkSession.Current.Enhanced);
    await BenchmarkPreflight.VerifyAsync(BenchmarkSession.Current.Enhanced, scenario.Method, scenario.Path, scenario.Body, transport: BenchmarkSession.Current.Transport, acceptGzip: BenchmarkSession.Current.AcceptGzip);
    await RunLoadAsync(BenchmarkSession.Current.Enhanced.Port, scenario, concurrency, TimeSpan.FromSeconds(warmupSeconds), null);
    await ResetBackendAsync();
    var result = await RunLoadAsync(BenchmarkSession.Current.Enhanced.Port, scenario, concurrency, TimeSpan.FromSeconds(durationSeconds), process, allowPolicyRejections: allowNonSuccess);
    var counters = await ReadBackendCountersAsync();
    var passed = result.Requests > 0 && result.Failures == 0;
    records.Add(ExtendedResult.FromLoad(group, scenario.Name, concurrency, result, counters.Total, passed, allowNonSuccess ? "Expected policy rejections included" : "All responses successful"));
    Console.WriteLine($"[{group}] {scenario.Name}: {result.RequestsPerSecond:N0} RPS, P95 {result.P95Ms:N2} ms, errors {result.ErrorRatePercent:N2}%");
    StopProcess(process);
    processes.Remove(process);
    await Task.Delay(500);
}

static async Task RunFunctionalCaseAsync(string group, string mode, string path, HttpStatusCode expectedStatus, long expectedBackendRequests, List<ExtendedResult> records, List<Process> processes, string dll, string root)
{
    var process = StartService(group, dll, root, [$"--mode={mode}"]);
    processes.Add(process);
    await WaitForHealthyAsync(new Uri(BenchmarkSession.Current.Enhanced, "/health").ToString(), process);
    await BenchmarkSession.Current.CaptureHostAsync("EnhancedYarp", BenchmarkSession.Current.Enhanced);
    await BenchmarkPreflight.VerifyAsync(BenchmarkSession.Current.Enhanced, HttpMethod.Get, "/api/perf/plain", transport: BenchmarkSession.Current.Transport, acceptGzip: BenchmarkSession.Current.AcceptGzip);
    await ResetBackendAsync();
    using var client = BenchmarkSession.Current.Transport.CreateClient();
    var started = Stopwatch.GetTimestamp();
    using var response = await client.GetAsync(new Uri(BenchmarkSession.Current.Enhanced, path));
    await response.Content.CopyToAsync(Stream.Null);
    var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    var counters = await ReadBackendCountersAsync();
    var passed = response.StatusCode == expectedStatus && counters.Total == expectedBackendRequests;
    records.Add(new ExtendedResult(group, path, 1, 1, latency, latency, latency, response.IsSuccessStatusCode ? 0 : 100, counters.Total, response.StatusCode.ToString(), passed, $"Expected status {(int)expectedStatus}, backend calls {expectedBackendRequests}"));
    Console.WriteLine($"[{group}] status {(int)response.StatusCode}, backend calls {counters.Total}: {(passed ? "PASS" : "FAIL")}");
    StopProcess(process);
    processes.Remove(process);
    await Task.Delay(500);
}

static async Task RunCircuitCaseAsync(List<ExtendedResult> records, List<Process> processes, string dll, string root)
{
    var process = StartService("circuit", dll, root, ["--mode=circuit"]);
    processes.Add(process);
    await WaitForHealthyAsync(new Uri(BenchmarkSession.Current.Enhanced, "/health").ToString(), process);
    await BenchmarkSession.Current.CaptureHostAsync("EnhancedYarp", BenchmarkSession.Current.Enhanced);
    await BenchmarkPreflight.VerifyAsync(BenchmarkSession.Current.Enhanced, HttpMethod.Get, "/api/perf/plain", transport: BenchmarkSession.Current.Transport, acceptGzip: BenchmarkSession.Current.AcceptGzip);
    await ResetBackendAsync();
    using var client = BenchmarkSession.Current.Transport.CreateClient();
    var statuses = new List<int>();
    for (var i = 0; i < 6; i++)
    {
        using var response = await client.GetAsync(new Uri(BenchmarkSession.Current.Enhanced, "/api/perf/status/503"));
        statuses.Add((int)response.StatusCode);
        await response.Content.CopyToAsync(Stream.Null);
    }
    var openCounters = await ReadBackendCountersAsync();
    await Task.Delay(2200);
    using var probe = await client.GetAsync(new Uri(BenchmarkSession.Current.Enhanced, "/api/perf/plain"));
    await probe.Content.CopyToAsync(Stream.Null);
    var recoveredCounters = await ReadBackendCountersAsync();
    var passed = statuses.All(x => x == 503) && openCounters.Total == 3 && probe.IsSuccessStatusCode && recoveredCounters.Total == 4;
    records.Add(new ExtendedResult("circuit", "open-half-open-closed", 1, 7, 0, 0, 0, 0, recoveredCounters.Total, string.Join('/', statuses) + $"/{(int)probe.StatusCode}", passed, "3 backend failures, 3 open rejections, successful half-open probe"));
    Console.WriteLine($"[circuit] backend before recovery {openCounters.Total}, after {recoveredCounters.Total}: {(passed ? "PASS" : "FAIL")}");
    StopProcess(process);
    processes.Remove(process);
    await Task.Delay(500);
}

static async Task ResetBackendAsync()
{
    using var client = BenchmarkSession.Current.Transport.CreateClient();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        using var response = await client.PostAsync(new Uri(BenchmarkSession.Current.Backend, "/api/perf/control/reset"), null, deadline.Token);
        if (response.StatusCode != HttpStatusCode.Conflict) { response.EnsureSuccessStatusCode(); return; }
        await Task.Delay(20, deadline.Token);
    }
}

static async Task<BackendCounters> ReadBackendCountersAsync()
{
    using var client = BenchmarkSession.Current.Transport.CreateClient();
    var json = await client.GetStringAsync(new Uri(BenchmarkSession.Current.Backend, "/api/perf/control/counters"));
    return JsonSerializer.Deserialize<BackendCounters>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendCounters();
}

static string BuildExtendedCsv(IEnumerable<ExtendedResult> records)
{
    var sb = new StringBuilder("Group,Scenario,Concurrency,RPS,P50Ms,P95Ms,P99Ms,NonSuccessPercent,BackendRequests,Statuses,Passed,Notes,ConfigurationHash\n");
    foreach (var x in records)
    {
        sb.AppendLine(string.Join(',', Csv(x.Group), Csv(x.Scenario), x.Concurrency, F(x.RequestsPerSecond), F(x.P50Ms), F(x.P95Ms), F(x.P99Ms), F(x.NonSuccessPercent), x.BackendRequests, Csv(x.Statuses), x.Passed, Csv(x.Notes), Csv(x.ConfigurationHash ?? "unavailable")));
    }

    return sb.ToString();
}

static string BuildExtendedMarkdown(IEnumerable<ExtendedResult> records, int durationSeconds, int soakSeconds)
{
    var rows = records.ToList();
    var sb = new StringBuilder("# Wafish First Batch Performance Results\n\n");
    sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    sb.AppendLine($"- Transport: {BenchmarkSession.Current.Transport.Description}; upstream: {(BenchmarkSession.Current.External ? "unverified" : "matched") }.");
    sb.AppendLine($"- Runtime: {Environment.Version}; processors: {Environment.ProcessorCount}");
    sb.AppendLine("- Latency: fixed-memory histogram; upper-bound percentiles, ≤1% relative error above 1 μs. See the adjacent JSON metadata.");
    sb.AppendLine($"- Regular duration: {durationSeconds}s; soak duration: {soakSeconds}s").AppendLine();
    sb.AppendLine("| Group | Scenario | C | RPS | P95 ms | P99 ms | Non-success % | Backend calls | Check |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
    foreach (var x in rows)
    {
        sb.AppendLine($"| {x.Group} | {x.Scenario} | {x.Concurrency} | {x.RequestsPerSecond:N0} | {x.P95Ms:N2} | {x.P99Ms:N2} | {x.NonSuccessPercent:N2} | {x.BackendRequests} | {(x.Passed ? "PASS" : "FAIL")} |");
    }

    sb.AppendLine().AppendLine("## Functional Assertions").AppendLine();
    foreach (var x in rows.Where(x => !string.IsNullOrEmpty(x.Notes)))
    {
        sb.AppendLine($"- {x.Group}/{x.Scenario}: {(x.Passed ? "PASS" : "FAIL")} - {x.Notes}; observed `{x.Statuses}`.");
    }

    sb.AppendLine().AppendLine("> Same-machine closed-loop benchmark. Policy rejection percentages are expected for rate-limit and WAF scenarios and are not transport failures.");
    return sb.ToString();
}

static Process StartService(string name, string dll, string workingDirectory, IReadOnlyList<string>? arguments = null, Uri? listenAddress = null)
{
    if (!File.Exists(dll))
    {
        throw new FileNotFoundException($"Build output for {name} was not found.", dll);
    }

    var info = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true, Environment = { ["DOTNET_ENVIRONMENT"] = "Production" }
    };
    info.ArgumentList.Add(dll);
    foreach (var argument in arguments ?? []) info.ArgumentList.Add(argument);
    var session = BenchmarkSession.Current;
    var project = Path.GetFileNameWithoutExtension(dll);
    var address = listenAddress ?? (project == "Backend" ? session.Backend : project == "NativeYarp" ? session.Native : session.Enhanced);
    info.ArgumentList.Add("--transport=" + session.Transport.Mode);
    if (session.Transport.CertificatePath is { } certificatePath) info.Environment[BenchmarkTransport.CertificateEnvironment] = certificatePath;
    info.ArgumentList.Add("--listen-url=" + address);
    info.ArgumentList.Add("--backend-urls=" + string.Join(',', session.Destinations.Addresses.Select(address => address.ToString())));
    info.ArgumentList.Add("--load-balancing-policy=" + session.Destinations.LoadBalancingPolicy);
    info.ArgumentList.Add("--payload-mode=" + session.PayloadMode);
    info.ArgumentList.Add("--data-root=" + Path.Combine(session.DataDirectory, Guid.NewGuid().ToString("N")));
    var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {name}.");
    _ = DrainAsync(process.StandardOutput);
    _ = DrainAsync(process.StandardError);
    return process;
}

static async Task DrainAsync(StreamReader reader)
{
    while (await reader.ReadLineAsync() != null) { }
}

static async Task WaitForHealthyAsync(string url, Process process)
{
    using var client = BenchmarkSession.Current.Transport.CreateClient(TimeSpan.FromSeconds(1));
    for (var i = 0; i < 60; i++)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Service exited during startup with code {process.ExitCode}.");
        }

        try
        {
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch { }
        await Task.Delay(250);
    }
    throw new TimeoutException($"Service did not become healthy: {url}");
}

static void StopProcess(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }
    catch { }
    process.Dispose();
}

static List<BenchmarkResult> BuildSummary(List<BenchmarkResult> results) => results
    .GroupBy(x => new { x.Gateway, x.Scenario })
    .Select(group =>
    {
        var ordered = group.OrderBy(x => x.RequestsPerSecond).ToArray();
        return ordered[ordered.Length / 2] with { Repetition = 0 };
    })
    .OrderBy(x => x.Scenario).ThenBy(x => x.Gateway).ToList();

static string BuildCsv(List<BenchmarkResult> results, List<BenchmarkResult> summary)
{
    var sb = new StringBuilder("Kind,Gateway,Scenario,Repetition,RPS,P50Ms,P95Ms,P99Ms,ErrorRatePercent,CpuPercent,WorkingSetMb,Requests,Failures,PolicyRejections,TransportFailures,ConfigurationHash,Offered,Started,Dropped,CancelledBeforeStart,OfferedRPS,CompletedResponses,QueueP95Ms,QueueP99Ms,ScheduledP95Ms,ScheduledP99Ms,ResponseBytes,BodyReads,FirstByteSamples,FirstByteP50Ms,FirstByteP95Ms,FirstByteP99Ms,ReadGapP95Ms,DestinationCount,LoadBalancingPolicy,DestinationRequests\n");
    foreach (var item in results.Select(x => (Kind: "run", Item: x)).Concat(summary.Select(x => (Kind: "median", Item: x))))
    {
        sb.AppendLine(string.Join(',', item.Kind, Csv(item.Item.Gateway), Csv(item.Item.Scenario), item.Item.Repetition, F(item.Item.RequestsPerSecond), F(item.Item.P50Ms), F(item.Item.P95Ms), F(item.Item.P99Ms), F(item.Item.ErrorRatePercent), F(item.Item.CpuPercent), F(item.Item.WorkingSetMb), item.Item.Requests, item.Item.Failures, item.Item.PolicyRejections, item.Item.TransportFailures, Csv(item.Item.ConfigurationHash ?? "unavailable"), item.Item.Arrivals.Offered, item.Item.Arrivals.Started, item.Item.Arrivals.Dropped, item.Item.Arrivals.CancelledBeforeStart, F(item.Item.Arrivals.OfferedPerSecond), item.Item.CompletedResponses, F(item.Item.Arrivals.QueueP95Ms), F(item.Item.Arrivals.QueueP99Ms), F(item.Item.ScheduledP95Ms), F(item.Item.ScheduledP99Ms), item.Item.ResponseBytes, item.Item.BodyReads, item.Item.FirstByteSamples, F(item.Item.FirstByteP50Ms), F(item.Item.FirstByteP95Ms), F(item.Item.FirstByteP99Ms), F(item.Item.ReadGapP95Ms), BenchmarkSession.Current.External ? "unavailable" : BenchmarkSession.Current.Destinations.Addresses.Length.ToString(CultureInfo.InvariantCulture), Csv(BenchmarkSession.Current.External ? "unavailable" : BenchmarkSession.Current.Destinations.LoadBalancingPolicy), Csv(item.Item.DestinationRequests is null ? "unavailable" : JsonSerializer.Serialize(item.Item.DestinationRequests))));
    }

    return sb.ToString();
}

static string BuildMarkdown(List<BenchmarkResult> summary, int duration, int warmup, int concurrency, int repetitions)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Wafish Performance Results").AppendLine();
    sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    sb.AppendLine($"- OS: {Environment.OSVersion}");
    sb.AppendLine($"- Logical processors: {Environment.ProcessorCount}");
    sb.AppendLine($"- Transport: {BenchmarkSession.Current.Transport.Description}; upstream: {(BenchmarkSession.Current.External ? "unverified" : "matched") }.");
    sb.AppendLine($"- Runtime: {Environment.Version}");
    sb.AppendLine($"- Concurrency: {concurrency}");
    if (BenchmarkSession.Current.PluginSets.Length > 0)
        sb.AppendLine("- Plugin sets: " + string.Join("; ", BenchmarkSession.Current.PluginSets.Select(set => set.Name)) + "; common Accept-Encoding: gzip. Effective settings and order are in JSON host manifests.");
    sb.AppendLine($"- Arrivals: {(BenchmarkSession.Current.ArrivalRate == 0 ? "closed-loop" : "scheduled")}; target: {BenchmarkSession.Current.ArrivalRate}/s; queue: {BenchmarkSession.Current.ArrivalQueueCapacity}.");
    sb.AppendLine("- Service latency starts at dispatch. Scheduled latency starts at the intended arrival. Queue delay includes scheduler lag.");
    sb.AppendLine("- Latency: fixed-memory histogram; upper-bound percentiles, ≤1% relative error above 1 μs.");
    sb.AppendLine("- Full environment and configuration manifests: adjacent JSON report.");
    sb.AppendLine($"- Warmup: {warmup}s; duration: {duration}s; repetitions: {repetitions}; result: median RPS run").AppendLine();
    sb.AppendLine("| Gateway | Scenario | RPS | P50 ms | P95 ms | P99 ms | Errors % | CPU % | Memory MB |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var item in summary)
    {
        sb.AppendLine($"| {item.Gateway} | {item.Scenario} | {item.RequestsPerSecond:N0} | {item.P50Ms:N2} | {item.P95Ms:N2} | {item.P99Ms:N2} | {item.ErrorRatePercent:N3} | {item.CpuPercent:N1} | {item.WorkingSetMb:N1} |");
    }

    sb.AppendLine().AppendLine("| Gateway | Scenario | Offered | Started | Dropped | Cancelled before start | Responses | Queue P95 ms | Scheduled P95 ms |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var item in summary)
        sb.AppendLine($"| {item.Gateway} | {item.Scenario} | {item.Arrivals.Offered} | {item.Arrivals.Started} | {item.Arrivals.Dropped} | {item.Arrivals.CancelledBeforeStart} | {item.CompletedResponses} | {item.Arrivals.QueueP95Ms:N2} | {item.ScheduledP95Ms:N2} |");
    sb.AppendLine().AppendLine("| Gateway | Scenario | Response bytes | Body reads | First-byte samples | First byte P95 ms | Completion P95 ms | Maximum read gap P95 ms |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
    foreach (var item in summary)
        sb.AppendLine($"| {item.Gateway} | {item.Scenario} | {item.ResponseBytes} | {item.BodyReads} | {item.FirstByteSamples} | {item.FirstByteP95Ms:N2} | {item.P95Ms:N2} | {item.ReadGapP95Ms:N2} |");
    if (!BenchmarkSession.Current.External)
    {
        sb.AppendLine().AppendLine($"Destinations: {BenchmarkSession.Current.Destinations.Addresses.Length}; load balancing: {BenchmarkSession.Current.Destinations.LoadBalancingPolicy}.");
        sb.AppendLine("Backend counts exclude preflight and warmup. Cache hits can reduce counts; retries can increase them.");
        sb.AppendLine().AppendLine("| Gateway | Scenario | Destination | Backend requests |").AppendLine("|---|---|---|---:|");
        foreach (var item in summary)
            foreach (var destination in item.DestinationRequests ?? new Dictionary<string, long>())
                sb.AppendLine($"| {item.Gateway} | {item.Scenario} | {destination.Key} | {destination.Value} |");
    }
    sb.AppendLine().AppendLine("## Relative throughput versus Native YARP").AppendLine();
    foreach (var scenario in summary.Select(x => x.Scenario).Distinct())
    {
        var baseline = summary.FirstOrDefault(x => x.Scenario == scenario && x.Gateway == "Native YARP");
        if (baseline == null)
        {
            continue;
        }

        foreach (var item in summary.Where(x => x.Scenario == scenario && x.Gateway != "Native YARP"))
        {
            sb.AppendLine($"- {item.Gateway} / {scenario}: {(item.RequestsPerSecond / baseline.RequestsPerSecond - 1) * 100:N1}% RPS, {(item.P99Ms / baseline.P99Ms - 1) * 100:N1}% P99 latency.");
        }
    }
    sb.AppendLine().AppendLine("> Same-machine benchmark. The report records the arrival model. Use the relative differences to assess middleware overhead; do not treat these numbers as production capacity planning without cross-machine load generation.");
    return sb.ToString();
}

static int ReadIntArgument(string[] args, string name, int fallback)
{
    var text = BenchmarkOptions.Read(args, name, fallback.ToString(CultureInfo.InvariantCulture));
    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException(name + " requires an integer.");
}

static string ReadStringArgument(string[] args, string name, string fallback)
{
    return BenchmarkOptions.Read(args, name, fallback);
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wafish.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
}

static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

sealed record GatewayCase(string Name, int Port, string Project, IReadOnlyList<string> Arguments);
sealed record Scenario(string Name, HttpMethod Method, string Path, string? Body)
{
    public static Scenario Get(string name, string path) => new(name, HttpMethod.Get, path, null);
    public static Scenario Post(string name, string path, string body) => new(name, HttpMethod.Post, path, body);
}
sealed record BenchmarkResult(string Gateway, string Scenario, int Repetition, double RequestsPerSecond, double P50Ms, double P95Ms, double P99Ms, double ErrorRatePercent, double CpuPercent, double WorkingSetMb, long Requests, long Failures, long PolicyRejections, long TransportFailures)
{
    public string? ConfigurationHash { get; init; }
    public IReadOnlyDictionary<string, long>? DestinationRequests { get; init; }
    public ArrivalStatistics Arrivals { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    public long CompletedResponses { get; init; }
    public double ScheduledP95Ms { get; init; }
    public double ScheduledP99Ms { get; init; }
    public long ResponseBytes { get; init; }
    public long BodyReads { get; init; }
    public long FirstByteSamples { get; init; }
    public double FirstByteP50Ms { get; init; }
    public double FirstByteP95Ms { get; init; }
    public double FirstByteP99Ms { get; init; }
    public double ReadGapP95Ms { get; init; }
}
sealed record ExtendedResult(string Group, string Scenario, int Concurrency, double RequestsPerSecond, double P50Ms, double P95Ms, double P99Ms, double NonSuccessPercent, long BackendRequests, string Statuses, bool Passed, string Notes)
{
    public string? ConfigurationHash { get; init; } = BenchmarkSession.Current.ConfigurationHash(BenchmarkSession.Current.Enhanced.Port);
    public static ExtendedResult FromLoad(string group, string scenario, int concurrency, BenchmarkResult result, long backendRequests, bool passed, string notes) =>
        new(group, scenario, concurrency, result.RequestsPerSecond, result.P50Ms, result.P95Ms, result.P99Ms, result.ErrorRatePercent, backendRequests, "load", passed, $"{notes}; expected 429: {result.PolicyRejections}; transport failures: {result.TransportFailures}");
}
sealed class BackendCounters
{
    public long Total { get; set; }
    public long FlakyAttempts { get; set; }
}
