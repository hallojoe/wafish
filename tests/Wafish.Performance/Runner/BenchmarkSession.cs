using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wafish.Performance;

internal sealed class BenchmarkSession : IDisposable
{
    public static BenchmarkSession Current { get; private set; } = null!;
    private readonly List<object> _hosts = [];
    private readonly Dictionary<int, string> _hashes = [];
    private string? _directory;
    private object _environment = null!;
    private readonly string _root;
    public BenchmarkPluginSelection[] PluginSets { get; }
    public bool AcceptGzip => PluginSets.Length > 0;
    public BenchmarkStreamWorkload? Streaming { get; }
    public int ArrivalRate { get; }
    public int ArrivalQueueCapacity { get; }
    public bool External { get; }
    public BenchmarkTransport Transport { get; }
    public Uri Backend { get; }
    public Uri Native { get; }
    public Uri Enhanced { get; }
    public string PayloadMode { get; }
    public string ResultsDirectory { get; }
    public string[]? Modes { get; }
    public string DataDirectory
    {
        get
        {
            if (_directory is not null) return _directory;
            _directory = Path.Combine(Path.GetTempPath(), "wafish-perf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return _directory;
        }
    }
    private BenchmarkSession(string[] args, string root)
    {
        _root = root;
        var external = BenchmarkOptions.Read(args, "--external-url", "");
        External = external.Length != 0;
        var mode = BenchmarkOptions.Read(args, "--transport", external.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https1" : "http1");
        Transport = new BenchmarkTransport(mode);
        Backend = Transport.Address(BenchmarkOptions.Read(args, "--backend-url", $"{Transport.Scheme}://127.0.0.1:5300"), "--backend-url");
        Native = Transport.Address(BenchmarkOptions.Read(args, "--native-url", $"{Transport.Scheme}://127.0.0.1:5301"), "--native-url");
        Enhanced = Transport.Address(BenchmarkOptions.Read(args, "--enhanced-url", $"{Transport.Scheme}://127.0.0.1:5302"), "--enhanced-url");
        if (new[] { Backend.Port, Native.Port, Enhanced.Port }.Distinct().Count() != 3 || Backend.Port == 0 || Native.Port == 0 || Enhanced.Port == 0)
            throw new ArgumentException("Managed listener ports must be distinct and nonzero.");
        var suite = BenchmarkOptions.Read(args, "--suite", "baseline");
        var matrix = BenchmarkOptions.Read(args, "--plugin-sets", suite == "plugins" ? BenchmarkPluginMatrix.DefaultSets : "");
        if (suite != "plugins" && matrix.Length > 0) throw new ArgumentException("--plugin-sets requires --suite=plugins.");
        if (suite == "plugins" && External) throw new ArgumentException("Plugin combinations require managed hosts.");
        PluginSets = suite == "plugins" ? BenchmarkPluginMatrix.Parse(matrix) : [];
        var workload = BenchmarkOptions.Read(args, "--workload", "baseline");
        if (workload != "baseline")
        {
            if (External) throw new ArgumentException("Use --path for external streaming endpoints.");
            Streaming = new BenchmarkStreamWorkload(workload,
                int.Parse(BenchmarkOptions.Read(args, "--stream-chunks", "8"), System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(BenchmarkOptions.Read(args, "--stream-bytes", "1024"), System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(BenchmarkOptions.Read(args, "--stream-interval", "50"), System.Globalization.CultureInfo.InvariantCulture));
        }
        ArrivalRate = int.Parse(BenchmarkOptions.Read(args, "--arrival-rate", "0"), System.Globalization.CultureInfo.InvariantCulture);
        ArrivalQueueCapacity = int.Parse(BenchmarkOptions.Read(args, "--arrival-queue", "128"), System.Globalization.CultureInfo.InvariantCulture);
        if (ArrivalRate is < 0 or > 100000 || ArrivalQueueCapacity is < 1 or > 65536)
            throw new ArgumentException("Arrival rate must be 0..100000; queue capacity must be 1..65536.");
        PayloadMode = BenchmarkOptions.Read(args, "--payload-mode", "precomputed");
        if (PayloadMode is not ("precomputed" or "generated")) throw new ArgumentException("Unknown payload mode.");
        ResultsDirectory = Path.GetFullPath(BenchmarkOptions.Read(args, "--results-directory", Path.Combine(root, "tests", "Wafish.Performance", "results")));
        var modes = BenchmarkOptions.Read(args, "--modes", "");
        Modes = string.IsNullOrWhiteSpace(modes) ? null : modes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (PluginSets.Length > 0 && Modes is not null) throw new ArgumentException("Use --plugin-sets instead of --modes for plugin comparisons.");
        if (Transport.Tls && external.Length == 0)
        {
            var path = Path.Combine(DataDirectory, "loopback.pfx");
            BenchmarkTransport.CreateCertificate(path);
            Transport = new BenchmarkTransport(mode, path);
        }

    }
    public static async Task<BenchmarkSession> CreateAsync(string[] args, string root)
    {
        var session = new BenchmarkSession(args, root);
        var revision = await session.GitAsync("rev-parse", "HEAD");
        var dirty = await session.GitAsync("status", "--porcelain");
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (OperatingSystem.IsLinux())
        {
            try { cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim(); }
            catch (IOException) { }
        }
        session._environment = new
        {
            capturedUtc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
            cpuModel = cpu ?? "unavailable", serverGc = GCSettings.IsServerGC, sourceRevision = revision?.Trim(),
            workingTreeDirty = dirty is null ? (bool?)null : !string.IsNullOrWhiteSpace(dirty)
        };
        Current = session;
        return session;
    }
    private async Task<string?> GitAsync(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return null; }
            await errors;
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
    public Uri AddressForPort(int port) => port == Backend.Port ? Backend : port == Native.Port ? Native : port == Enhanced.Port ? Enhanced : throw new ArgumentException("Unknown managed port.");
    public async Task CaptureHostAsync(string name, Uri endpoint)
    {
        using var client = Transport.CreateClient(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(new Uri(endpoint, "/benchmark/manifest"));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var manifest = body.RootElement.Clone();
        _hashes[endpoint.Port] = manifest.GetProperty("configurationHash").GetString()!;
        _hosts.Add(new { name, endpoint = endpoint.ToString(), manifest });
    }
    public string? ConfigurationHash(int port) => _hashes.GetValueOrDefault(port);
    public async Task WriteAsync(string path, object results, object settings)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 5, environment = _environment, settings, streaming = Streaming,
            pluginSets = PluginSets, acceptEncoding = AcceptGzip ? "gzip" : "identity",
            arrivals = new { mode = ArrivalRate == 0 ? "closed-loop" : "scheduled", ratePerSecond = ArrivalRate, queueCapacity = ArrivalQueueCapacity, drainDeadlineSeconds = 12 },
            transport = new { mode = Transport.Mode, client = Transport.Description, upstream = External ? "unverified external upstream" : Transport.Description, certificate = !Transport.Tls ? "none" : Transport.CertificatePath is null ? "system trust" : "temporary pinned loopback certificate" },
            latency = new { strategy = "fixed logarithmic histogram; upper-bound nearest rank", relativeError = BoundedLatencyHistogram.RelativeError,
                absoluteErrorBelowMinimumMs = BoundedLatencyHistogram.MinimumMilliseconds, maximumMs = BoundedLatencyHistogram.MaximumMilliseconds,
                retainedBytes = new BoundedLatencyHistogram().RetainedBytes },
            hosts = _hosts, results
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    public void Dispose()
    {
        Transport.Dispose();
        if (_directory is not null) Directory.Delete(_directory, recursive: true);
    }
}
