using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GatewayGrpc = Wafish.GatewayRegistry.GatewayRegistry;
using Wafish.GatewayRegistry;
using Wafish.Models;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wafish.Services;

/// <summary>
/// Gateway auto-registration client. Local services use this to register routes with a remote gateway,
/// so traffic goes through the gateway pipeline before forwarding to the local service.
///
/// Config priority (high to low): code > env vars > appsettings.json.
/// </summary>
public partial class GatewayAutoRegistrationClient
{
    private long _registrationVersion = -1;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GatewayRegistrationOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GatewayAutoRegistrationClient> _logger;
    private readonly KestrelAutoConfigService _kestrelConfig;
    private readonly GatewayGrpc.GatewayRegistryClient _grpcClient;

    /// <summary>Initializes a new instance of GatewayAutoRegistrationClient.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Gateway registration options.</param>
    /// <param name="serviceProvider">Service provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="kestrelConfig">Kestrel auto-configuration service.</param>
    /// <param name="grpcClient">gRPC gateway registry client.</param>
    public GatewayAutoRegistrationClient(
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayRegistrationOptions> options,
        IServiceProvider serviceProvider,
        ILogger<GatewayAutoRegistrationClient> logger,
        KestrelAutoConfigService kestrelConfig,
        GatewayGrpc.GatewayRegistryClient grpcClient)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _kestrelConfig = kestrelConfig;
        _grpcClient = grpcClient;
    }

    /// <summary>Query optional gRPC capabilities. An empty version-zero response identifies a legacy server.</summary>
    public async Task<CapabilitiesResponse> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await GrpcClient.GetCapabilitiesAsync(new CapabilitiesRequest(), BuildGrpcCallOptions(ct)).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            return new CapabilitiesResponse();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Register this service's route with the gateway.</summary>
    public async Task<bool> RegisterAsync(CancellationToken ct = default) => (await RegisterResultAsync(ct).ConfigureAwait(false)).Success;

    /// <summary>Register with revision and retry information. Caller cancellation propagates.</summary>
    private async Task<RegistrationResult> RegisterCoreAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!RegistrationOptionsResolver.IsEnabled(_options))
        {
            _logger.LogDebug("Auto-registration disabled (no GatewayUrl configured)");
            return new(false, RegistrationStatus.Disabled, "Auto-registration is disabled.");
        }

        if (_options.LeaseDurationSeconds != 0 && (_options.LeaseDurationSeconds is < 15 or > 3600 || _options.UseGrpcRegistration != true
            || string.IsNullOrWhiteSpace(_options.InstanceId) || _options.HeartbeatEnabled == false) || _options.DrainSeconds is < 0 or > 300)
            return new(false, RegistrationStatus.ValidationRejected, "Leases require gRPC, InstanceId, heartbeats, and 15–3600 seconds. DrainSeconds must be 0–300.");
        var gatewayUrl = GatewayUrl;
        var routeName = RegistrationOptionsResolver.GetRouteName(_options);
        var clusterName = RegistrationOptionsResolver.GetClusterName(_options);
        var matchPath = RegistrationOptionsResolver.GetMatchPath(_options);
        var destinationAddress = RegistrationOptionsResolver.GetDestinationAddress(_options, _serviceProvider);
        var order = RegistrationOptionsResolver.GetOrder(_options);
        var transforms = RegistrationOptionsResolver.GetTransforms(_options);
        var useIpIsolation = RegistrationOptionsResolver.UseIpIsolation(_options);
        var useGrpcRegistration = _options.UseGrpcRegistration == true;

        if (string.IsNullOrWhiteSpace(gatewayUrl) || string.IsNullOrWhiteSpace(destinationAddress))
        {
            _logger.LogWarning("Registration info incomplete, skipping");
            return new(false, RegistrationStatus.ValidationRejected, "Gateway URL and destination address are required.");
        }

        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            _logger.LogDebug("Registering: Route={RouteName}, Match={MatchPath}, Dest={DestAddress}, GW={GatewayUrl}",
                routeName, matchPath, destinationAddress, gatewayUrl);
        }

        if (transforms != null && transforms.Count > 0)
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                _logger.LogDebug("Transforms: {Transforms}", JsonSerializer.Serialize(transforms));
            }
        }

        if (useIpIsolation)
        {
            _logger.LogDebug("IP-based isolation enabled. Gateway will route based on client IP address.");
        }

        if (RegistrationOptionsResolver.GetAutoResolveIp(_options))
        {
            var (resolvedAddress, warning) = ResolveLocalAddressWithCheck(destinationAddress);
            destinationAddress = resolvedAddress;

            if (!string.IsNullOrWhiteSpace(warning))
            {
                _logger.LogWarning("{RegistrationWarning}", warning);
            }

            try
            {
                var uri = new Uri(destinationAddress);
                _kestrelConfig.EnsureListeningOnAny(uri.Port);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to auto-configure Kestrel");
            }
        }

        try
        {
            if (useGrpcRegistration)
            {
                return await RegisterWithGrpcAsync(routeName, clusterName, destinationAddress, ct).ConfigureAwait(false);
            }

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(RegistrationOptionsResolver.GetTimeoutSeconds(_options));
            ApplyAuthHeaders(http);

            var json = JsonSerializer.Serialize(new
            {
                routeName,
                clusterName,
                matchPath,
                destinationAddress,
                order,
                transforms,
                useIpIsolation,
                clientIp = useIpIsolation ? GetLocalIpv4() : null
            }, _jsonOptions);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{gatewayUrl.TrimEnd('/')}/api/gateway/register-route";
            using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            return await ReadResultAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, RegistrationStatus.DeadlineExceeded, "Registration timed out. Inspect the gateway before retrying."); }
        catch (HttpRequestException) { return new(false, RegistrationStatus.Unavailable, "Gateway transport failed. Inspect the gateway before retrying."); }
        catch (RpcException ex) { return RpcResult(ex, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected registration error");
            return new(false, RegistrationStatus.InternalError, "Registration failed. Inspect the gateway before retrying.");
        }
    }

    /// <summary>Unregister this service's route from the gateway.</summary>
    public async Task<bool> UnregisterAsync(CancellationToken ct = default)
    {
        var result = await UnregisterResultAsync(ct).ConfigureAwait(false);
        return result.Success || result.Status == RegistrationStatus.NotFound;
    }

    /// <summary>Unregister with revision and retry information. Caller cancellation propagates.</summary>
    private async Task<RegistrationResult> UnregisterCoreAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(GatewayUrl)) return new(false, RegistrationStatus.Disabled, "Gateway URL is not configured.");
        var routeName = RegistrationOptionsResolver.GetRouteName(_options);
        try
        {
            if (_options.UseGrpcRegistration == true)
            {
                var response = await GrpcClient.UnregisterServiceAsync(new UnregisterServiceRequest
                    { ServiceId = routeName, InstanceId = _options.InstanceId ?? "", LeaseToken = _options.LeaseDurationSeconds > 0 ? _leaseToken : "" }, BuildGrpcCallOptions(ct)).ConfigureAwait(false);
                return RegistrationResult.Remote(response.Success, response.Message, response.Status,
                    response.HasPersistedVersion ? response.PersistedVersion : null, response.HasActiveVersion ? response.ActiveVersion : null);
            }
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(RegistrationOptionsResolver.GetTimeoutSeconds(_options));
            ApplyAuthHeaders(http);
            var url = $"{GatewayUrl.TrimEnd('/')}/api/gateway/{Uri.EscapeDataString(routeName)}";
            var clientIp = RegistrationOptionsResolver.UseIpIsolation(_options) ? GetLocalIpv4() : null;
            if (!string.IsNullOrWhiteSpace(clientIp)) url += $"?clientIp={Uri.EscapeDataString(clientIp)}";
            using var responseHttp = await http.DeleteAsync(url, ct).ConfigureAwait(false);
            return await ReadResultAsync(responseHttp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, RegistrationStatus.DeadlineExceeded, "Unregistration timed out. Inspect the gateway before retrying."); }
        catch (HttpRequestException) { return new(false, RegistrationStatus.Unavailable, "Gateway transport failed. Inspect the gateway before retrying."); }
        catch (RpcException ex) { return RpcResult(ex, ct); }
    }

    private async Task<RegistrationResult> RegisterWithGrpcAsync(
        string routeName,
        string clusterName,
        string destinationAddress,
        CancellationToken ct)
    {
        try
        {
            // Phase 2 contract: multi-path (one route per path, shared cluster),
            // multi-destination (all join the cluster for load balancing),
            // load balancing policy and metadata.
            if (_options.LeaseDurationSeconds > 0 && !(await GetCapabilitiesAsync(ct).ConfigureAwait(false)).Capabilities.Contains("instance-leases"))
                return new(false, RegistrationStatus.ValidationRejected, "The gateway does not support registration leases.");
            var paths = RegistrationOptionsResolver.GetMatchPaths(_options);
            var extraDestinations = RegistrationOptionsResolver.GetExtraDestinationAddresses(_options);
            var metadata = RegistrationOptionsResolver.GetMetadata(_options);

            var request = new RegisterServiceRequest
            {
                ServiceId = routeName,
                LeaseDurationSeconds = _options.LeaseDurationSeconds,
                LeaseToken = _options.LeaseDurationSeconds > 0 ? _leaseToken : "",
                        InstanceId = _options.InstanceId ?? "",
                ServiceName = clusterName,
                LoadBalancing = MapLoadBalancingPolicy(_options.LoadBalancingPolicy)
            };
            request.Paths.AddRange(paths);
            request.Metadata.Add(metadata);

            request.Destinations.Add(new Destination
            {
                DestinationId = "d1",
                Address = destinationAddress,
                Enabled = true
            });
            for (int i = 0; i < extraDestinations.Count; i++)
            {
                request.Destinations.Add(new Destination
                {
                    DestinationId = $"d{i + 2}",
                    Address = extraDestinations[i],
                    Enabled = true
                });
            }

            var callOptions = BuildGrpcCallOptions(ct);
            var response = await GrpcClient.RegisterServiceAsync(request, callOptions).ConfigureAwait(false);
            if (response.Success && _options.LeaseDurationSeconds > 0 && response.Lease is null)
                return new(false, RegistrationStatus.Unknown, "The gateway omitted the negotiated lease; registration ownership is uncertain.");
            if (response.Success) Volatile.Write(ref _lease, response.Lease?.Clone());
            if (response.Success && response.HasActiveVersion)
            {
                Interlocked.Exchange(ref _registrationVersion, response.ActiveVersion);
            }

            return RegistrationResult.Remote(response.Success, response.Message, response.Status,
                response.HasPersistedVersion ? response.PersistedVersion : null, response.HasActiveVersion ? response.ActiveVersion : null);
        }
        catch (RpcException ex) { return RpcResult(ex, ct); }
    }

    /// <summary>
    /// Map a load balancing policy name (case-insensitive) to the proto enum.
    /// Unknown/null values fall back to PowerOfTwoChoices (proto default).
    /// </summary>
    private static GatewayRegistry.LoadBalancingPolicy MapLoadBalancingPolicy(string? policy)
    {
        return policy?.Trim().ToLowerInvariant() switch
        {
            "random" => GatewayRegistry.LoadBalancingPolicy.Random,
            "roundrobin" or "round_robin" => GatewayRegistry.LoadBalancingPolicy.RoundRobin,
            "leastrequests" or "least_requests" => GatewayRegistry.LoadBalancingPolicy.LeastRequests,
            "poweroftwochoices" or "power_of_two_choices" => GatewayRegistry.LoadBalancingPolicy.PowerOfTwoChoices,
            _ => GatewayRegistry.LoadBalancingPolicy.PowerOfTwoChoices
        };
    }

    private void ApplyAuthHeaders(HttpClient http)
    {
        if (!string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AuthToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.BasicAuthUsername) && !string.IsNullOrWhiteSpace(_options.BasicAuthPassword))
        {
            var creds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.BasicAuthUsername}:{_options.BasicAuthPassword}"));
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }
    }

    /// <summary>
    /// Build gRPC call options with auth metadata from <see cref="GatewayRegistrationOptions"/>.
    /// Supports Bearer token, Basic auth, and API Key.
    /// </summary>
    private CallOptions BuildGrpcCallOptions(CancellationToken ct = default)
    {
        var metadata = new Metadata();

        if (!string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            metadata.Add("Authorization", $"Bearer {_options.AuthToken}");
        }
        else if (!string.IsNullOrWhiteSpace(_options.BasicAuthUsername) && !string.IsNullOrWhiteSpace(_options.BasicAuthPassword))
        {
            var creds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.BasicAuthUsername}:{_options.BasicAuthPassword}"));
            metadata.Add("Authorization", $"Basic {creds}");
        }
        else if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            metadata.Add("x-api-key", _options.ApiKey);
        }

        return new CallOptions(headers: metadata, deadline: DateTime.UtcNow.AddSeconds(RegistrationOptionsResolver.GetTimeoutSeconds(_options)), cancellationToken: ct);
    }

    /// <summary>Resolve localhost/127.0.0.1/0.0.0.0 in destination to LAN IPv4 with listening check.</summary>
    /// <returns>Tuple of (resolved address, warning message if any).</returns>
    private (string address, string? warning) ResolveLocalAddressWithCheck(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (address, null);
        }

        // Wildcard host bindings (* and +) are valid Kestrel/ASP.NET Core ways to listen
        // on all interfaces (both IPv4 and IPv6), but Uri/UriBuilder reject them because
        // they are not legal DNS names. Normalize to localhost purely for scheme/port
        // extraction — the original binding semantics are tracked via the wildcard flag
        // so the "listening on all interfaces" check still runs correctly.
        var parsedAddress = address;
        var isWildcardHost = TryNormalizeWildcardHost(ref parsedAddress);

        try
        {
            var uri = new Uri(parsedAddress);
            var host = uri.Host;
            var isLocalOrAny = isWildcardHost ||
                host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("127.0.0.1") || host.Equals("0.0.0.0") ||
                host.Equals("::1") || host.Equals("[::1]");

            if (isLocalOrAny)
            {
                bool isListeningOnAny = IsListeningOnAnyAddress(uri.Port);
                var ip = GetLocalIpv4();
                if (ip != null)
                {
                    var resolved = $"{uri.Scheme}://{ip}:{uri.Port}{uri.PathAndQuery}";

                    if (!isListeningOnAny)
                    {
                        var warning = $"Service is listening on localhost only (port {uri.Port}), but registered with LAN IP {ip}. " +
                            $"Cross-machine access will fail until you configure Kestrel to listen on all interfaces. " +
                            $"Use a wildcard binding (covers both IPv4 and IPv6) or 0.0.0.0 (IPv4 only):\n" +
                            $"  - launchSettings.json: \"applicationUrl\": \"http://*:{uri.Port}\" (or http://+:{uri.Port})\n" +
                            $"  - Or Program.cs: .UseUrls(\"http://*:{uri.Port}\")";

                        return (resolved, warning);
                    }

                    return (resolved, null);
                }
            }
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Invalid URI format in address: {Address}", address);
        }

        return (address, null);
    }

    /// <summary>
    /// Detect Kestrel/ASP.NET Core wildcard host bindings (<c>*</c> and <c>+</c>) in an
    /// <c>applicationUrl</c>-style value and rewrite the host to <c>localhost</c> so it can be
    /// parsed by <see cref="Uri"/>. Returns <c>true</c> when a wildcard host was found and
    /// replaced (the caller then treats the address as "listening on all interfaces").
    /// Non-wildcard values are returned unchanged with <c>false</c>.
    /// </summary>
    /// <param name="address">The URL to normalize; modified in place when a wildcard host is found.</param>
    /// <returns><c>true</c> if a wildcard host (<c>*</c>/<c>+</c>) was replaced; otherwise <c>false</c>.</returns>
    private static bool TryNormalizeWildcardHost(ref string address)
    {
        // Match "http(s)://*:port..." or "http(s)://+:port..." — the wildcard appears as the host
        // segment right after the scheme authority prefix. Keep it tight so a literal "*" in a
        // path/query is never touched.
        var colonSlash = address.IndexOf("://", StringComparison.OrdinalIgnoreCase);
        if (colonSlash < 0)
        {
            return false;
        }

        var hostStart = colonSlash + 3;
        if (hostStart >= address.Length)
        {
            return false;
        }

        var wc = address[hostStart];
        if (wc != '*' && wc != '+')
        {
            return false;
        }

        // Ensure the wildcard is the host (followed by ':' or '/' or end), not part of a name.
        var afterHost = hostStart + 1;
        if (afterHost < address.Length && address[afterHost] != ':' && address[afterHost] != '/')
        {
            return false;
        }

        var scheme = address.Substring(0, hostStart);
        var rest = address.Substring(afterHost);
        address = scheme + "localhost" + rest;
        return true;
    }

    /// <summary>Check if the service is listening on 0.0.0.0 (all interfaces) for the given port.</summary>
    private static bool IsListeningOnAnyAddress(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(l => l.Port == port);

            foreach (var listener in listeners)
            {
                if (listener.Address.Equals(IPAddress.Any) ||
                    listener.Address.Equals(IPAddress.IPv6Any))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? GetLocalIpv4()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                ?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Send a heartbeat to the gateway to keep the registration alive.
    /// Gateway uses heartbeat to detect stale registrations.
    /// </summary>
    private async Task<bool> HeartbeatCoreAsync(CancellationToken ct = default)
    {
        var gatewayUrl = GatewayUrl;
        var routeName = RegistrationOptionsResolver.GetRouteName(_options);
        var useGrpcRegistration = _options.UseGrpcRegistration == true;

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return false;
        }

        try
        {
            if (useGrpcRegistration)
            {
                try
                {
                    var callOptions = BuildGrpcCallOptions(ct);
                    var grpcResponse = await GrpcClient.HeartbeatAsync(new HeartbeatRequest
                    {
                        ServiceId = routeName,
                        InstanceId = _options.InstanceId ?? "",
                        LeaseToken = _options.LeaseDurationSeconds > 0 ? _leaseToken : ""
                    }, callOptions).ConfigureAwait(false);

                    if (grpcResponse.Success && _options.LeaseDurationSeconds > 0 && grpcResponse.Lease is null) return false;
                    if (grpcResponse.Success)
                    {
                        Volatile.Write(ref _lease, grpcResponse.Lease?.Clone());
                        if (grpcResponse.HasActiveVersion) Interlocked.Exchange(ref _registrationVersion, grpcResponse.ActiveVersion);
                    }
                    return grpcResponse.Success;
                }
                catch (RpcException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogWarning(ex, "gRPC heartbeat RPC error: {Status}", ex.Status);
                    return false;
                }
            }

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            ApplyAuthHeaders(http);

            var clientIp = RegistrationOptionsResolver.UseIpIsolation(_options) ? GetLocalIpv4() : null;
            var url = $"{gatewayUrl.TrimEnd('/')}/api/gateway/{routeName}/heartbeat";
            if (!string.IsNullOrWhiteSpace(clientIp))
            {
                url += $"?clientIp={Uri.EscapeDataString(clientIp)}";
            }

            using var response = await http.PostAsync(url, null, ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Query services registered on the gateway via gRPC (requires <c>UseGrpcRegistration</c>).
    /// Returns null when gRPC registration mode is disabled or the call fails.
    /// </summary>
    public async Task<IReadOnlyList<ServiceInfo>?> GetServicesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        if (_options.UseGrpcRegistration != true)
        {
            _logger.LogDebug("GetServicesAsync is only available in gRPC registration mode");
            return null;
        }

        try
        {
            var callOptions = BuildGrpcCallOptions(ct);
            var response = await GrpcClient.GetServicesAsync(new GetServicesRequest
            {
                ActiveOnly = activeOnly
            }, callOptions).ConfigureAwait(false);

            return response.Services.ToList();
        }
        catch (RpcException ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "gRPC GetServices RPC error: {Status}", ex.Status);
            return null;
        }
    }

    /// <summary>
    /// Update the destinations of this service's cluster on the gateway via gRPC
    /// (requires <c>UseGrpcRegistration</c>). Pass the full destination set — it replaces the existing one.
    /// </summary>
    public async Task<bool> UpdateDestinationsAsync(IReadOnlyList<string> addresses, CancellationToken ct = default)
        => (await UpdateDestinationsResultAsync(addresses, ct).ConfigureAwait(false)).Success;

    /// <summary>Replace owned destinations with explicit revisions and retry guidance.</summary>
    public async Task<RegistrationResult> UpdateDestinationsResultAsync(IReadOnlyList<string> addresses, CancellationToken ct = default)
    {
        await _registrationOperations.WaitAsync(ct).ConfigureAwait(false);
        try { return await UpdateDestinationsCoreAsync(addresses, ct).ConfigureAwait(false); }
        finally { _registrationOperations.Release(); }
    }

    private async Task<RegistrationResult> UpdateDestinationsCoreAsync(IReadOnlyList<string> addresses, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_options.RegistrationEndpoints.Count > 0 && !_endpointMayOwnRegistration)
            return new(false, RegistrationStatus.NotFound, "No endpoint owns this registration.");
        if (_options.UseGrpcRegistration != true)
        {
            _logger.LogWarning("UpdateDestinationsAsync is only available in gRPC registration mode");
            return new(false, RegistrationStatus.Disabled, "Destination updates require gRPC registration.");
        }

        if (addresses == null || addresses.Count == 0)
        {
            _logger.LogWarning("UpdateDestinationsAsync requires at least one address");
            return new(false, RegistrationStatus.ValidationRejected, "At least one destination address is required.");
        }

        try
        {
            var request = new UpdateDestinationsRequest { ServiceId = RegistrationOptionsResolver.GetRouteName(_options),
                InstanceId = _options.InstanceId ?? "", LeaseToken = _options.LeaseDurationSeconds > 0 ? _leaseToken : "", ExpectedVersion = Interlocked.Read(ref _registrationVersion) };
            for (int i = 0; i < addresses.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(addresses[i]))
                {
                    continue;
                }

                request.Destinations.Add(new Destination
                {
                    DestinationId = $"d{i + 1}",
                    Address = addresses[i].Trim(),
                    Enabled = true
                });
            }

            var callOptions = BuildGrpcCallOptions(ct);
            var response = await GrpcClient.UpdateDestinationsAsync(request, callOptions).ConfigureAwait(false);
            if (response.Success && response.HasActiveVersion)
            {
                Interlocked.Exchange(ref _registrationVersion, response.ActiveVersion);
            }

            if (!response.Success)
            {
                _logger.LogWarning("gRPC UpdateDestinations failed: {Message}", response.Message);
            }

            return RegistrationResult.Remote(response.Success, response.Message, response.Status,
                response.HasPersistedVersion ? response.PersistedVersion : null, response.HasActiveVersion ? response.ActiveVersion : null);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "gRPC UpdateDestinations RPC error: {Status}", ex.Status);
            return RpcResult(ex, ct);
        }
    }
}
