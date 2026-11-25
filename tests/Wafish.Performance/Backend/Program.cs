using System.Text;
using System.Text.Json;
using Wafish.Performance;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
using var transport = BenchmarkTransport.Read(args);
var address = transport.Address(BenchmarkOptions.Read(args, "--listen-url", $"{transport.Scheme}://127.0.0.1:5300"), "--listen-url");
var transportFaults = bool.Parse(BenchmarkOptions.Read(args, "--transport-faults", "false"));
var payloadMode = BenchmarkOptions.Read(args, "--payload-mode", "precomputed");
if (payloadMode is not ("precomputed" or "generated")) throw new ArgumentException("Unknown payload mode.");
builder.WebHost.ConfigureBenchmarkListener(address, transport);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
builder.Services.AddSingleton<BackendState>();
var app = builder.Build();

var plain = "OK"u8.ToArray();
var smallJson = JsonSerializer.SerializeToUtf8Bytes(new
{
    id = 1, name = "Wafish performance payload", timestamp = "2026-01-01T00:00:00Z", values = Enumerable.Range(1, 32).ToArray()
});
int[] preparedSizes = [0, 1024, 65536, 1048576, 4194304];
var payloads = payloadMode == "precomputed" ? preparedSizes.ToDictionary(size => size, size => Encoding.ASCII.GetBytes(new string('x', size))) : [];
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Benchmark-Protocol"] = context.Request.Protocol;
    var measured = context.Request.Path.StartsWithSegments("/api/perf") && !context.Request.Path.StartsWithSegments("/api/perf/control");
    var state = context.RequestServices.GetRequiredService<BackendState>();
    if (measured) state.Start();
    var aborted = false;
    try { await next(); }
    catch (BadHttpRequestException error) when (error.StatusCode == 413) { context.Response.StatusCode = 413; }
    catch (IOException) { aborted = true; throw; }
    finally
    {
        if (measured) state.Finish(context.Request.Path, context.Response.StatusCode, context.RequestAborted.IsCancellationRequested, aborted || context.Items.ContainsKey("Benchmark.TransportAbort"));
    }
});
app.MapGet("/health", () => Results.Ok());
app.MapGet("/benchmark/manifest", () => Results.Json(BenchmarkManifest.Create(new { mode = "backend", payloadMode, preparedSizes, maxRequestBytes = 4194304, maxDelayMilliseconds = 30000, transportFaults, streamLimits = new { maxChunks = 256, maxChunkBytes = 65536, maxIntervalMs = 1000, maxDurationMs = 4000, maxBytes = 4194304 }, transport = transport.Description })));
app.MapGet("/api/perf/plain", () => Results.Bytes(plain, "text/plain"));
app.MapGet("/api/perf/json-small", () => Results.Bytes(smallJson, "application/json"));
app.MapGet("/api/perf/payload/{size:int}", (int size, HttpContext context) =>
{
    var length = Math.Clamp(size, 0, 4 * 1024 * 1024);
    var prepared = payloads.TryGetValue(length, out var bytes);
    context.Response.Headers["X-Benchmark-PayloadMode"] = prepared ? "precomputed" : "generated";
    return Results.Bytes(bytes ?? Encoding.ASCII.GetBytes(new string('x', length)), "application/octet-stream");
});
app.MapPost("/api/perf/echo", async (HttpRequest request) =>
{
    const int limit = 4 * 1024 * 1024;
    using var memory = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var count = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted);
        if (count == 0) break;
        if (memory.Length + count > limit) return Results.StatusCode(413);
        await memory.WriteAsync(buffer.AsMemory(0, count), request.HttpContext.RequestAborted);
    }
    return Results.Bytes(memory.ToArray(), request.ContentType ?? "application/octet-stream");
});
app.MapGet("/api/perf/delay/{milliseconds:int}", async (int milliseconds, HttpContext context) =>
{
    await Task.Delay(Math.Clamp(milliseconds, 0, 30_000), context.RequestAborted);
    return Results.Bytes(plain, "text/plain");
});
app.MapGet("/api/perf/stream/{kind}/{chunks:int}/{chunkBytes:int}/{intervalMs:int}", async (string kind, int chunks, int chunkBytes, int intervalMs, HttpContext context) =>
{
    BenchmarkStreamWorkload workload;
    try { workload = new(kind, chunks, chunkBytes, intervalMs); }
    catch (ArgumentException) { context.Response.StatusCode = 400; return; }
    context.Response.ContentType = kind == "events" ? "text/event-stream" : "text/plain";
    context.Response.Headers.CacheControl = "no-store";
    var chunk = workload.CreateChunk();
    for (var index = 0; index < workload.Chunks; index++)
    {
        if (index > 0) await Task.Delay(workload.IntervalMs, context.RequestAborted);
        await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});
app.MapGet("/api/perf/status/{statusCode:int}", (int statusCode) => Results.StatusCode(Math.Clamp(statusCode, 100, 599)));
app.MapGet("/api/perf/flaky/{failCount:int}", (int failCount, BackendState state) => state.NextFlakyAttempt() <= Math.Max(0, failCount)
    ? Results.StatusCode(503) : Results.Bytes(plain, "text/plain"));
if (transportFaults)
{
    app.MapGet("/api/perf/fault/truncate/{bytes:int}", async (int bytes, HttpContext context) =>
    {
        if (bytes is < 1 or > 1048576) { context.Response.StatusCode = 400; return; }
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = bytes + 1;
        context.Items["Benchmark.TransportAbort"] = true;
        await context.Response.Body.WriteAsync(new byte[bytes], context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        context.Abort();
    });
    app.MapGet("/api/perf/fault/stall/{milliseconds:int}", async (int milliseconds, HttpContext context) =>
    {
        if (milliseconds is < 1 or > 30000) { context.Response.StatusCode = 400; return; }
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = 1;
        await context.Response.StartAsync(context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await Task.Delay(milliseconds, context.RequestAborted);
        await context.Response.Body.WriteAsync(new byte[1], context.RequestAborted);
    });
}
app.MapGet("/api/perf/control/counters", (BackendState state) => Results.Json(state.Snapshot()));
app.MapPost("/api/perf/control/reset", (BackendState state) => state.TryReset() ? Results.NoContent() : Results.StatusCode(409));
app.Run();

sealed class BackendState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, long> _statuses = [];
    private long _total, _flakyAttempts, _active, _cancelled, _aborted;
    public long NextFlakyAttempt() => Interlocked.Increment(ref _flakyAttempts);
    public void Start() { lock (_gate) _active++; }
    public void Finish(string path, int statusCode, bool cancelled, bool aborted)
    {
        lock (_gate)
        {
            _active--; _total++;
            if (aborted) _aborted++;
            else if (cancelled) _cancelled++;
            _paths[path] = _paths.GetValueOrDefault(path) + 1;
            _statuses[statusCode] = _statuses.GetValueOrDefault(statusCode) + 1;
        }
    }
    public object Snapshot()
    {
        lock (_gate) return new
        {
            total = _total, active = _active, cancelled = _cancelled, aborted = _aborted, completed = _total - _cancelled - _aborted,
            flakyAttempts = Interlocked.Read(ref _flakyAttempts), paths = _paths.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
            statuses = _statuses.OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(), x => x.Value)
        };
    }
    public bool TryReset()
    {
        lock (_gate)
        {
            if (_active != 0) return false;
            _total = _cancelled = _aborted = 0; Interlocked.Exchange(ref _flakyAttempts, 0);
            _paths.Clear(); _statuses.Clear(); return true;
        }
    }
}
