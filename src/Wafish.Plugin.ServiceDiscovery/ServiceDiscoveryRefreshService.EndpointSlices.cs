using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Wafish.Models;

namespace Wafish.Plugin.ServiceDiscovery;

public sealed partial class ServiceDiscoveryRefreshService
{
    private async Task<IReadOnlyList<string>> ResolveEndpointSlicesAsync(ServiceDiscoveryExecutionConfig config, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/"
            || string.IsNullOrWhiteSpace(config.ServiceName) || string.IsNullOrWhiteSpace(config.Namespace))
            throw new InvalidOperationException("EndpointSlice discovery requires an HTTP(S) API origin, serviceName, and namespace.");
        if (config.UseServiceAccountToken && origin.Scheme != "https")
            throw new InvalidOperationException("Service-account credentials require HTTPS.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 1, 60)), _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var client = _httpClientFactory.CreateClient("service-discovery");
        var baseUrl = origin.AbsoluteUri.TrimEnd('/') + "/apis/discovery.k8s.io/v1/namespaces/" + Uri.EscapeDataString(config.Namespace)
            + "/endpointslices?limit=100&labelSelector=" + Uri.EscapeDataString("kubernetes.io/service-name=" + config.ServiceName);
        string? token = null;
        if (config.UseServiceAccountToken)
        {
            token = (await File.ReadAllTextAsync("/var/run/secrets/kubernetes.io/serviceaccount/token", timeout.Token)).Trim();
            if (token.Length is 0 or > 16384 || token.Any(char.IsControl)) throw new InvalidOperationException("Invalid service-account credential.");
        }
        var endpoints = new List<string>();
        var continuations = new HashSet<string>(StringComparer.Ordinal);
        var next = "";
        string? resourceVersion = null;
        long totalBytes = 0;
        for (var page = 0; page < 100; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + (next.Length == 0 ? "" : "&continue=" + Uri.EscapeDataString(next)));
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");
            Interlocked.Increment(ref _httpRequests);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
            {
                totalBytes += read;
                if (buffer.Length + read > 4 * 1024 * 1024 || totalBytes > 16 * 1024 * 1024)
                    throw new JsonException("EndpointSlice response exceeds its bounded refresh budget.");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (GetString(root, "kind") != "EndpointSliceList" || GetString(root, "apiVersion") != "discovery.k8s.io/v1")
                throw new JsonException("Expected a discovery.k8s.io/v1 EndpointSliceList.");
            var metadata = RequiredProperty(root, "metadata");
            var version = GetString(metadata, "resourceVersion");
            if (page > 0 && version != resourceVersion) throw new JsonException("EndpointSlice pages changed resource version.");
            resourceVersion = version;
            var items = RequiredProperty(root, "items"); RequireArray(items);
            foreach (var slice in items.EnumerateArray()) ReadEndpointSlice(config, slice, endpoints);
            next = GetString(metadata, "continue") ?? "";
            if (next.Length == 0) return NormalizeEndpoints(endpoints);
            if (next.Length > 4096 || !continuations.Add(next)) throw new JsonException("EndpointSlice continuation is invalid or repeated.");
        }
        throw new JsonException("EndpointSlice page limit exceeded.");
    }

    private static void ReadEndpointSlice(ServiceDiscoveryExecutionConfig config, JsonElement slice, List<string> results)
    {
        RequireObject(slice);
        var metadata = RequiredProperty(slice, "metadata");
        if (GetString(metadata, "namespace") != config.Namespace || GetString(RequiredProperty(metadata, "labels"), "kubernetes.io/service-name") != config.ServiceName)
            throw new JsonException("EndpointSlice does not match the requested service and namespace.");
        var family = GetString(slice, "addressType") switch
        {
            "IPv4" => AddressFamily.InterNetwork,
            "IPv6" => AddressFamily.InterNetworkV6,
            _ => throw new JsonException("EndpointSlice addressType must be IPv4 or IPv6.")
        };
        var ports = RequiredProperty(slice, "ports"); RequireArray(ports);
        var selectedPorts = new List<int>();
        foreach (var port in ports.EnumerateArray())
        {
            RequireObject(port);
            var protocol = GetString(port, "protocol");
            if (protocol is not (null or "TCP") || config.PortName is not null && GetString(port, "name") != config.PortName) continue;
            var number = GetInt(port, "port");
            if (number is not (> 0 and <= 65535)) throw new JsonException("EndpointSlice requires a concrete TCP port.");
            selectedPorts.Add(number.Value);
        }
        var endpoints = RequiredProperty(slice, "endpoints"); RequireArray(endpoints);
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            RequireObject(endpoint);
            if (endpoint.TryGetProperty("conditions", out var conditions))
            {
                RequireObject(conditions);
                if (!Condition(conditions, "ready", true) || !Condition(conditions, "serving", true) || Condition(conditions, "terminating", false)) continue;
            }
            var addresses = RequiredProperty(endpoint, "addresses"); RequireArray(addresses);
            foreach (var address in addresses.EnumerateArray())
            {
                if (address.ValueKind != JsonValueKind.String || !IPAddress.TryParse(address.GetString(), out var ip) || ip.AddressFamily != family)
                    throw new JsonException("EndpointSlice contains an invalid IP address.");
                foreach (var port in selectedPorts)
                {
                    if (results.Count >= 10000) throw new JsonException("EndpointSlice endpoint limit exceeded.");
                    AddHostPort(results, config.Scheme == "https" ? "https" : "http", ip.ToString(), port);
                }
            }
        }
    }

    private static bool Condition(JsonElement conditions, string name, bool fallback)
        => !conditions.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? fallback
            : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
            : throw new JsonException("EndpointSlice conditions must be boolean or null.");
}
