using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wafish.Performance;

internal static partial class BenchmarkReliabilityChecks
{
    private static async Task PreflightAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/wrong", () => "wrong");
        app.MapGet("/api/perf/plain", () => "wrong");
        app.MapGet("/api/perf/json-small", () => "not JSON");
        app.MapGet("/api/perf/payload/12", () => "not twelve x");
        app.MapGet("/error", () => Results.StatusCode(503));
        app.MapGet("/oversized", async context => { var block = new byte[65536]; for (var index = 0; index < 65; index++) await context.Response.Body.WriteAsync(block, context.RequestAborted); });
        app.MapGet("/stall", async context => { await context.Response.StartAsync(); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); });
        await app.StartAsync(); var endpoint = new Uri(app.Urls.Single());
        foreach (var path in new[] { "/api/perf/plain", "/api/perf/json-small", "/api/perf/payload/12", "/error", "/oversized" })
        {
            try { await BenchmarkPreflight.VerifyAsync(endpoint, HttpMethod.Get, path, knownWorkload: path.StartsWith("/api")); throw new Exception("Invalid preflight succeeded: " + path); }
            catch (InvalidOperationException) { }
        }
        await BenchmarkPreflight.VerifyAsync(endpoint, HttpMethod.Get, "/wrong", knownWorkload: false);
        using var cancel = new CancellationTokenSource();
        var pending = BenchmarkPreflight.VerifyAsync(endpoint, HttpMethod.Get, "/stall", knownWorkload: false, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Stalled preflight ignored cancellation."); } catch (OperationCanceledException) { }
        await app.StopAsync();
    }
    private static async Task BackendAsync()
    {
        await using var backend = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress(), "--payload-mode=precomputed"]);
        using var client = new HttpClient { BaseAddress = backend.Address, Timeout = TimeSpan.FromSeconds(10) };
        using var cancellation = new CancellationTokenSource();
        var delayed = client.GetAsync("/api/perf/delay/30000", cancellation.Token);
        await UntilAsync(async () => (await Counters(client)).GetProperty("active").GetInt64() == 1);
        using (var busyReset = await client.PostAsync("/api/perf/control/reset", null)) Check(busyReset.StatusCode == HttpStatusCode.Conflict, "Reset crossed an active workload.");
        cancellation.Cancel();
        try { await delayed; throw new Exception("Cancelled backend delay succeeded."); } catch (OperationCanceledException) { }
        await UntilAsync(async () => (await Counters(client)).GetProperty("active").GetInt64() == 0);
        Check((await Counters(client)).GetProperty("cancelled").GetInt64() == 1, "Backend delay outlived cancellation or lost its outcome.");
        using (var reset = await client.PostAsync("/api/perf/control/reset", null)) Check(reset.IsSuccessStatusCode, "Idle backend could not reset.");
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, backend.Address.Port);
            var bytes = Encoding.ASCII.GetBytes("POST /api/perf/echo HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n100\r\nx");
            await socket.GetStream().WriteAsync(bytes);
            await UntilAsync(async () => (await Counters(client)).GetProperty("active").GetInt64() == 1);
        }
        await UntilAsync(async () => (await Counters(client)).GetProperty("active").GetInt64() == 0);
        var afterEcho = await Counters(client);
        Check(afterEcho.GetProperty("cancelled").GetInt64() + afterEcho.GetProperty("aborted").GetInt64() == 1, "Interrupted echo work was not accounted.");
        await BenchmarkPreflight.VerifyAsync(backend.Address, HttpMethod.Get, "/api/perf/plain");
        await BenchmarkPreflight.VerifyAsync(backend.Address, HttpMethod.Get, "/api/perf/json-small");
        await BenchmarkPreflight.VerifyAsync(backend.Address, HttpMethod.Post, "/api/perf/echo", "echo payload");
        await BenchmarkPreflight.VerifyAsync(backend.Address, HttpMethod.Get, "/api/perf/payload/1024");
        using var prepared = await client.GetAsync("/api/perf/payload/1024");
        Check(prepared.Headers.GetValues("X-Benchmark-PayloadMode").Single() == "precomputed", "Known payload was generated during measurement.");
        using var generated = await client.GetAsync("/api/perf/payload/17");
        Check(generated.Headers.GetValues("X-Benchmark-PayloadMode").Single() == "generated" && (await generated.Content.ReadAsByteArrayAsync()).Length == 17, "Generated payload cost was not identified.");
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/perf/echo") { Content = new ByteArrayContent(new byte[4194305]) };
        oversizedRequest.Headers.ExpectContinue = true;
        using (var oversized = await client.SendAsync(oversizedRequest))
            Check((int)oversized.StatusCode == 413, "Backend accepted an oversized echo request.");
        Check((await Counters(client)).GetProperty("statuses").GetProperty("413").GetInt64() == 1, "Oversized echo was counted with the wrong status.");
        await using var other = await BenchmarkProcess.StartAsync("Backend", ["--listen-url=" + FreeAddress(), "--payload-mode=generated"]);
        using var otherClient = new HttpClient { BaseAddress = other.Address };
        using var uncached = await otherClient.GetAsync("/api/perf/payload/1024");
        Check(uncached.Headers.GetValues("X-Benchmark-PayloadMode").Single() == "generated", "Explicit generated mode used prepared bytes.");
        Check((await Counters(otherClient)).GetProperty("total").GetInt64() == 1, "Parallel backend counters were shared.");
    }
    private static async Task<JsonElement> Counters(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/perf/control/counters")); return json.RootElement.Clone();
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    private static Uri FreeAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
    }
    private sealed class BenchmarkProcess : IAsyncDisposable
    {
        public Process Process { get; }
        public Uri Address { get; }
        public string DirectoryPath => _directory;
        private readonly Task<string> _output, _error;
        private readonly string _directory;
        private BenchmarkProcess(Process process, Uri address, string directory)
        { Process = process; Address = address; _directory = directory; _output = process.StandardOutput.ReadToEndAsync(); _error = process.StandardError.ReadToEndAsync(); }
        public static string Root
        {
            get { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !File.Exists(Path.Combine(path.FullName, "Wafish.slnx"))) path = path.Parent; return path?.FullName ?? throw new DirectoryNotFoundException(); }
        }
        public static async Task<BenchmarkProcess> StartAsync(string project, string[] arguments, bool ready = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-benchmark-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.Combine(Root, "tests/Wafish.Performance", project, "bin/Release/net10.0", project + ".dll"));
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--data-root=" + directory);
            info.Environment["TMPDIR"] = directory;
            if (project == "Runner") info.ArgumentList.Add("--results-directory=" + Path.Combine(directory, "results"));
            var address = new Uri(BenchmarkOptions.Read(arguments, "--listen-url", "http://127.0.0.1:1"));
            var owned = new BenchmarkProcess(Process.Start(info)!, address, directory);
            try
            {
                if (ready)
                {
                    using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(1) };
                    await UntilAsync(async () =>
                    {
                        if (owned.Process.HasExited) throw new Exception("Benchmark host failed: " + await owned._error);
                        try { using var response = await client.GetAsync("/health"); return response.IsSuccessStatusCode; } catch (HttpRequestException) { return false; }
                    });
                }
                return owned;
            }
            catch { await owned.DisposeAsync(); throw; }
        }
        public async Task<(int ExitCode, string Output)> FinishAsync()
        { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); return (Process.ExitCode, await _output + await _error); }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(_output, _error); Process.Dispose(); Directory.Delete(_directory, true);
        }
    }
}
