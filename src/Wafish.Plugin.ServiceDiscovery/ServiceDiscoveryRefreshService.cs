using System.Collections.Concurrent;
using System.Text.Json;
using Wafish.Models;
using Wafish.Plugins;
using Wafish.Services;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.ServiceDiscovery;

/// <summary>Refreshes cluster destinations for enabled service-discovery bindings.</summary>
public sealed partial class ServiceDiscoveryRefreshService : IPluginRuntimeResource
{
    public const string PluginId = "service-discovery";

    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly WafishProxyConfigProvider _configProvider;
    private readonly IGatewaySnapshotCompiler _snapshotCompiler;
    private readonly IGatewaySnapshotPublisher _snapshotPublisher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ServiceDiscoveryRefreshService> _logger;
    private SemaphoreSlim PublishLock => GatewayPublicationCoordinator.For(_snapshotPublisher).Gate;
    private readonly ConcurrentDictionary<string, BindingState> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<double> _jitter;

    private sealed record BindingState(PluginBindingSnapshot? Binding, ServiceDiscoveryExecutionConfig Config, long FirstSeen)
    {
        public long LastAttempt { get; init; }
        public TimeSpan RetryDelay { get; init; }
        public long? LastSuccess { get; init; }
        public DateTimeOffset? LastSuccessUtc { get; init; }
        public int Failures { get; init; }
        public string? FailureType { get; init; }
        public bool TrafficRejected { get; init; }
    }
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stoppedAt;
    private Exception? _lastError;
    private long _refreshes;
    private long _publishedSnapshots;
    private long _httpRequests;

    string IPluginRuntimeResource.PluginId => PluginId;
    public string ResourceId => "service-discovery:endpoint-refresh";
    public string ResourceType => "endpoint-refresh";

    private readonly TimeProvider _time;
    public ServiceDiscoveryRefreshService(GatewayPluginExecutionPlanProvider executionPlans, WafishProxyConfigProvider configProvider, IGatewaySnapshotCompiler snapshotCompiler, IGatewaySnapshotPublisher snapshotPublisher, IHttpClientFactory httpClientFactory, ILogger<ServiceDiscoveryRefreshService> logger)
        : this(executionPlans, configProvider, snapshotCompiler, snapshotPublisher, httpClientFactory, logger, TimeProvider.System) { }

    public ServiceDiscoveryRefreshService(
        GatewayPluginExecutionPlanProvider executionPlans,
        WafishProxyConfigProvider configProvider,
        IGatewaySnapshotCompiler snapshotCompiler,
        IGatewaySnapshotPublisher snapshotPublisher,
        IHttpClientFactory httpClientFactory,
        ILogger<ServiceDiscoveryRefreshService> logger, TimeProvider timeProvider)
        : this(executionPlans, configProvider, snapshotCompiler, snapshotPublisher, httpClientFactory, logger, timeProvider, Random.Shared.NextDouble) { }

    internal ServiceDiscoveryRefreshService(
        GatewayPluginExecutionPlanProvider executionPlans, WafishProxyConfigProvider configProvider,
        IGatewaySnapshotCompiler snapshotCompiler, IGatewaySnapshotPublisher snapshotPublisher,
        IHttpClientFactory httpClientFactory, ILogger<ServiceDiscoveryRefreshService> logger,
        TimeProvider timeProvider, Func<double> jitter)
    {
        _jitter = jitter;
        _time = timeProvider;
        _executionPlans = executionPlans;
        _configProvider = configProvider;
        _snapshotCompiler = snapshotCompiler;
        _snapshotPublisher = snapshotPublisher;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public ValueTask StartResourceAsync(CancellationToken cancellationToken)
    {
        if (_runTask is { IsCompleted: false })
        {
            return ValueTask.CompletedTask;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startedAt = _time.GetUtcNow();
        _stoppedAt = null;
        _lastError = null;
        _runTask = RunAsync(_runCancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopResourceAsync(CancellationToken cancellationToken)
    {
        var cancellation = Interlocked.Exchange(ref _runCancellation, null);
        var task = Interlocked.Exchange(ref _runTask, null);
        if (cancellation == null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (task != null)
        {
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        cancellation.Dispose();
        _bindings.Clear();
        _stoppedAt = _time.GetUtcNow();
    }

    public ValueTask<PluginRuntimeResourceSnapshot> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var running = _runTask is { IsCompleted: false };
        var bindings = GetBindingHealth();
        var failures = bindings.Count(b => b.ConsecutiveFailures > 0);
        var stale = bindings.Count(b => b.Stale);
        var health = _lastError != null || failures > 0 || stale > 0 ? PluginResourceHealthStatus.Degraded :
            running ? PluginResourceHealthStatus.Healthy : PluginResourceHealthStatus.Stopped;
        var message = _lastError?.GetType().Name ?? (failures > 0 || stale > 0
            ? $"Discovery bindings: {failures} failing, {stale} stale." : null);
        return ValueTask.FromResult(new PluginRuntimeResourceSnapshot(
            ResourceId, ResourceType, running, health, _startedAt, _stoppedAt, message,
            new Dictionary<string, long>
            {
                ["refreshes"] = Interlocked.Read(ref _refreshes),
                ["publishedSnapshots"] = Interlocked.Read(ref _publishedSnapshots),
                ["httpRequests"] = Interlocked.Read(ref _httpRequests),
                ["memoryBytes"] = bindings.Count * 512L,
                ["bindings"] = bindings.Count,
                ["failedBindings"] = failures,
                ["staleBindings"] = stale,
                ["rejectedBindings"] = bindings.Count(b => b.TrafficRejected),
                ["maxStaleAgeSeconds"] = bindings.Count == 0 ? 0 : (long)bindings.Max(b => b.StaleAgeSeconds)
            }));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueBindingsAsync(cancellationToken).ConfigureAwait(false);
                _lastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _lastError = exception;
                _logger.LogError("Service discovery refresh cycle failed ({FailureType}).", exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<ServiceDiscoveryBindingHealth> GetBindingHealth() => _bindings
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair => new ServiceDiscoveryBindingHealth(pair.Key, pair.Value.LastSuccessUtc,
            _time.GetElapsedTime(pair.Value.LastSuccess ?? pair.Value.FirstSeen).TotalSeconds,
            pair.Value.Failures, pair.Value.FailureType, IsStale(pair.Value), pair.Value.TrafficRejected,
            Math.Max(0, (pair.Value.RetryDelay - _time.GetElapsedTime(pair.Value.LastAttempt)).TotalSeconds)))
        .ToArray();

    private bool IsStale(BindingState state) => state.Config.MaxStaleSeconds > 0 &&
        _time.GetElapsedTime(state.LastSuccess ?? state.FirstSeen) >= TimeSpan.FromSeconds(state.Config.MaxStaleSeconds);

    internal async Task RefreshDueBindingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedSnapshot = _snapshotPublisher.Current;
        var plan = _executionPlans.Current;
        if (plan.SnapshotVersion != capturedSnapshot.Version) { return; }

        var bindings = plan.ServiceDiscoveryByCluster.Where(pair => pair.Value.Enabled).ToArray();
        var activeClusters = bindings.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _bindings.Keys.Where(clusterId => !activeClusters.Contains(clusterId)))
        {
            _bindings.TryRemove(stale, out _);
        }

        foreach (var (clusterId, config) in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = GatewayPublicationCoordinator.DiscoveryBinding(capturedSnapshot, clusterId);
            if (!_bindings.TryGetValue(clusterId, out var state) || state.Binding != binding)
            {
                state = new BindingState(binding, config, _time.GetTimestamp());
                _bindings[clusterId] = state;
            }

            try
            {
                // Expiry is independent of retry backoff and never changes persisted configuration.
                state = await ApplyStalePolicyAsync(clusterId, state, cancellationToken).ConfigureAwait(false);
                _bindings[clusterId] = state;
                if (_time.GetElapsedTime(state.LastAttempt) < state.RetryDelay) { continue; }

                var endpoints = await ResolveEndpointsAsync(config, cancellationToken).ConfigureAwait(false);
                if (!await PublishDestinationsAsync(clusterId, endpoints, cancellationToken, binding).ConfigureAwait(false))
                {
                    _bindings.TryRemove(clusterId, out _);
                    continue;
                }
                state = state with
                {
                    LastSuccess = _time.GetTimestamp(), LastSuccessUtc = _time.GetUtcNow(),
                    LastAttempt = _time.GetTimestamp(), RetryDelay = TimeSpan.FromSeconds(Math.Clamp(config.RefreshSeconds, 5, 3600)),
                    Failures = 0, FailureType = null, TrafficRejected = false
                };
                _bindings[clusterId] = state;
                Interlocked.Increment(ref _refreshes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var failures = Math.Min(30, state.Failures + 1);
                var interval = Math.Clamp(config.RefreshSeconds, 5, 3600);
                var cap = Math.Max(interval, 300);
                var delay = Math.Min(cap, interval * Math.Pow(2, Math.Min(10, failures - 1))) * (0.8 + 0.2 * _jitter());
                state = state with
                {
                    Failures = failures, FailureType = exception.GetType().Name,
                    LastAttempt = _time.GetTimestamp(), RetryDelay = TimeSpan.FromSeconds(delay)
                };
                _bindings[clusterId] = state;
                _logger.LogWarning("Service discovery binding for cluster {ClusterId} failed ({FailureType}).", clusterId, state.FailureType);
                // A request timeout can cross the stale boundary during this attempt.
                _bindings[clusterId] = await ApplyStalePolicyAsync(clusterId, state, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<BindingState> ApplyStalePolicyAsync(string clusterId, BindingState state, CancellationToken cancellationToken)
    {
        if (!state.TrafficRejected && IsStale(state) &&
            string.Equals(state.Config.StalePolicy, "RejectNewTraffic", StringComparison.OrdinalIgnoreCase) &&
            await PublishDestinationsAsync(clusterId, [], cancellationToken, state.Binding).ConfigureAwait(false))
        {
            return state with { TrafficRejected = true };
        }
        return state;
    }

    internal async Task<IReadOnlyList<string>> ResolveEndpointsAsync(ServiceDiscoveryExecutionConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(config.Mode, "KubernetesEndpointSlice", StringComparison.OrdinalIgnoreCase))
            return await ResolveEndpointSlicesAsync(config, cancellationToken).ConfigureAwait(false);
        if (string.Equals(config.Mode, "Static", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeEndpoints(config.StaticEndpoints);
        }

        if (!Uri.TryCreate(BuildProviderUri(config), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{config.Mode} service discovery requires an absolute HTTP or HTTPS endpoint.");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 1, 60)), _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Interlocked.Increment(ref _httpRequests);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (string.Equals(config.Mode, "Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            var tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
            if (File.Exists(tokenPath))
            {
                request.Headers.Authorization = new("Bearer", await File.ReadAllTextAsync(tokenPath, timeout.Token));
            }
        }
        if (string.Equals(config.Mode, "Eureka", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
        }

        using var client = _httpClientFactory.CreateClient("service-discovery");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token).ConfigureAwait(false);
        return NormalizeEndpoints(ReadProviderEndpoints(config, document.RootElement));
    }

    private static string BuildProviderUri(ServiceDiscoveryExecutionConfig config)
    {
        if (string.Equals(config.Mode, "HttpJson", StringComparison.OrdinalIgnoreCase))
        {
            return config.Endpoint ?? "";
        }

        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ServiceName))
        {
            throw new InvalidOperationException($"{config.Mode} requires endpoint and serviceName.");
        }

        var root = config.Endpoint.TrimEnd('/');
        var service = Uri.EscapeDataString(config.ServiceName);
        return config.Mode.ToUpperInvariant() switch
        {
            "CONSUL" => $"{root}/v1/health/service/{service}?passing=true",
            "NACOS" => $"{root}/nacos/v1/ns/instance/list?serviceName={service}&healthyOnly=true",
            "EUREKA" => $"{root}/eureka/apps/{service}",
            "KUBERNETES" => $"{root}/api/v1/namespaces/{Uri.EscapeDataString(config.Namespace)}/endpoints/{service}",
            _ => throw new InvalidOperationException($"Unsupported service discovery mode '{config.Mode}'.")
        };
    }

    private static IEnumerable<string> ReadProviderEndpoints(ServiceDiscoveryExecutionConfig config, JsonElement root)
    {
        var scheme = config.Scheme is "https" ? "https" : "http";
        if (string.Equals(config.Mode, "HttpJson", StringComparison.OrdinalIgnoreCase)) { return ReadEndpointValues(root); }
        var results = new List<string>();
        switch (config.Mode.ToUpperInvariant())
        {
            case "CONSUL":
                RequireArray(root);
                foreach (var item in root.EnumerateArray())
                {
                    RequireObject(item);
                    if (item.TryGetProperty("Checks", out var checks))
                    {
                        RequireArray(checks);
                        if (checks.EnumerateArray().Any(check => !string.Equals(GetString(check, "Status"), "passing", StringComparison.OrdinalIgnoreCase))) { continue; }
                    }
                    var address = GetString(item, "Service", "Address");
                    AddHostPort(results, scheme, string.IsNullOrWhiteSpace(address) ? GetString(item, "Node", "Address") : address,
                        GetInt(item, "Service", "Port"));
                }
                break;
            case "NACOS":
                var hosts = RequiredProperty(root, "hosts");
                RequireArray(hosts);
                foreach (var item in hosts.EnumerateArray())
                {
                    RequireObject(item);
                    if ((!item.TryGetProperty("healthy", out var healthy) || healthy.GetBoolean()) &&
                        (!item.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()))
                    {
                        AddHostPort(results, scheme, GetString(item, "ip"), GetInt(item, "port"));
                    }
                }
                break;
            case "EUREKA":
                var instances = RequiredProperty(RequiredProperty(root, "application"), "instance");
                if (instances.ValueKind == JsonValueKind.Array)
                {
                    foreach (var instance in instances.EnumerateArray()) { AddEurekaInstance(results, scheme, instance); }
                }
                else { RequireObject(instances); AddEurekaInstance(results, scheme, instances); }
                break;
            case "KUBERNETES":
                RequireObject(root);
                if (!root.TryGetProperty("subsets", out var subsets) && GetString(root, "kind") == "Endpoints") { break; }
                RequireArray(subsets);
                foreach (var subset in subsets.EnumerateArray())
                {
                    RequireObject(subset);
                    // A subset can contain only notReadyAddresses. Those endpoints must not receive traffic.
                    if (!subset.TryGetProperty("addresses", out var addresses)) { continue; }
                    RequireArray(addresses);
                    var ports = RequiredProperty(subset, "ports");
                    RequireArray(ports);
                    foreach (var address in addresses.EnumerateArray())
                    {
                        foreach (var port in ports.EnumerateArray())
                        {
                            var protocol = GetString(port, "protocol");
                            if (protocol != null && protocol != "TCP") { continue; }
                            AddHostPort(results, scheme, GetString(address, "ip"), GetInt(port, "port"));
                        }
                    }
                }
                break;
            default: throw new JsonException("Unsupported discovery provider.");
        }
        return results;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new JsonException("Expected a discovery object."); }
    }

    private static void RequireArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) { throw new JsonException("Expected a discovery array."); }
    }

    private static JsonElement RequiredProperty(JsonElement value, string name)
    {
        RequireObject(value);
        return value.TryGetProperty(name, out var property) ? property : throw new JsonException("Missing discovery response field.");
    }

    private static void AddHostPort(List<string> results, string scheme, string? host, int? port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is not (> 0 and <= 65535))
        {
            throw new JsonException("Invalid discovery host or port.");
        }
        results.Add(new UriBuilder(scheme, host, port.Value).Uri.AbsoluteUri);
    }

    private static void AddEurekaInstance(List<string> results, string scheme, JsonElement instance)
    {
        RequireObject(instance);

        // Only expose healthy instances; a missing status is treated as UP for compatibility.
        if (TryPath(instance, out var status, "status") && status.ValueKind == JsonValueKind.String &&
            !string.Equals(status.GetString(), "UP", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var host = GetString(instance, "ipAddr") ?? GetString(instance, "hostName");
        var port = GetEurekaPort(instance, "port", secure: false)
            ?? GetEurekaPort(instance, "securePort", secure: true);
        if (port == null)
        {
            if (!instance.TryGetProperty("port", out _) && !instance.TryGetProperty("securePort", out _))
            {
                throw new JsonException("Missing Eureka port.");
            }
            return;
        }

        AddHostPort(results, port.Value.Secure ? "https" : scheme, host, port.Value.Port);
    }

    private static (int Port, bool Secure)? GetEurekaPort(JsonElement instance, string propertyName, bool secure)
    {
        // Eureka wraps ports as {"$": 8080, "@enabled": "true"}; honor the flag, defaulting to enabled when absent.
        var enabled = GetString(instance, propertyName, "@enabled");
        if (enabled != null && (!bool.TryParse(enabled, out var isEnabled) || !isEnabled))
        {
            return null;
        }

        var port = GetInt(instance, propertyName, "$");
        return port is > 0 and <= 65535 ? (port.Value, secure) : null;
    }

    private static string? GetString(JsonElement element, params string[] path) => TryPath(element, out var value, path) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt(JsonElement element, params string[] path) => TryPath(element, out var value, path) && (value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out var number) : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) ? number : null;
    private static bool TryPath(JsonElement element, out JsonElement value, params string[] path)
    { value = element; foreach (var part in path) { if (part == "$" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$", out var wrapped)) { value = wrapped; continue; } if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) { return false; } } return true; }

    private async Task<bool> PublishDestinationsAsync(string clusterId, IReadOnlyList<string> endpoints, CancellationToken cancellationToken, PluginBindingSnapshot? expectedBinding = null)
    {
        await PublishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var binding = GatewayPublicationCoordinator.DiscoveryBinding(_snapshotPublisher.Current, clusterId);
            if (expectedBinding != null && binding != expectedBinding)
            {
                return false;
            }

            var routes = _configProvider.GetRoutes();
            var clusters = _configProvider.GetClusters();
            var index = clusters.Select((cluster, position) => (cluster, position))
                .FirstOrDefault(item => string.Equals(item.cluster.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));
            if (index.cluster == null)
            {
                return false;
            }

            var destinations = endpoints.Select((address, position) => new KeyValuePair<string, DestinationConfig>(
                    "discovery-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(address))), new DestinationConfig { Address = address }))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (DestinationsEqual(index.cluster.Destinations, destinations)) { return true; }

            var updatedClusters = clusters.ToArray();
            updatedClusters[index.position] = index.cluster with { Destinations = destinations };
            var version = _snapshotPublisher.Current.Version + 1;
            var snapshot = await _snapshotCompiler.CompileAsync(routes, updatedClusters, version, cancellationToken).ConfigureAwait(false);
            _snapshotPublisher.Publish(snapshot);
            if (binding != null)
            {
                GatewayPublicationCoordinator.For(_snapshotPublisher).RememberDiscovery(clusterId, binding, destinations);
            }

            _configProvider.Update(snapshot.Routes, snapshot.Clusters);
            Interlocked.Increment(ref _publishedSnapshots);
            return true;
        }
        finally
        {
            PublishLock.Release();
        }
    }

    private static IEnumerable<string> ReadEndpointValues(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("endpoints", out var endpoints))
        {
            root = endpoints;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Service discovery response must be an array or an object containing an endpoints array.");
        }

        foreach (var item in root.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : GetString(item, "address");
            if (string.IsNullOrWhiteSpace(value)) { throw new JsonException("Invalid discovery endpoint entry."); }
            yield return value;
        }
    }

    private static string[] NormalizeEndpoints(IEnumerable<string> endpoints) => endpoints
        .Select(value => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri : throw new JsonException("Discovery endpoints must use absolute HTTP or HTTPS URLs."))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private static bool DestinationsEqual(
        IReadOnlyDictionary<string, DestinationConfig>? current,
        Dictionary<string, DestinationConfig> next)
    {
        if (current == null || current.Count != next.Count)
        {
            return false;
        }

        return current.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value.Address))
            .SequenceEqual(next.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value.Address)));
    }
}
