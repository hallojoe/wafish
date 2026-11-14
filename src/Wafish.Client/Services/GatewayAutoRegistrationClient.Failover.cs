using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Models;
using GatewayGrpc = Wafish.GatewayRegistry.GatewayRegistry;

namespace Wafish.Services;

public partial class GatewayAutoRegistrationClient
{
    private readonly SemaphoreSlim _registrationOperations = new(1, 1);
    private GatewayGrpc.GatewayRegistryClient? _endpointClient;
    private string[]? _endpoints;
    private int _endpointIndex;
    private bool _endpointMayOwnRegistration;
    private GatewayGrpc.GatewayRegistryClient GrpcClient => Volatile.Read(ref _endpointClient) ?? _grpcClient;
    private string? GatewayUrl => _endpoints is { Length: > 0 } endpoints ? endpoints[_endpointIndex] : _options.GatewayUrl;

    /// <summary>Current gRPC endpoint for ordered registration. Null means the legacy single endpoint configuration.</summary>
    public string? RegistrationEndpoint => _endpoints is { Length: > 0 } endpoints ? endpoints[_endpointIndex] : null;

    private async Task<RegistrationResult> RegisterOrderedAsync(CancellationToken ct)
    {
        if (_options.RegistrationEndpoints.Count == 0 || !RegistrationOptionsResolver.IsEnabled(_options)) return await RegisterCoreAsync(ct).ConfigureAwait(false);
        if (ValidateEndpoints(_options) is { } error) return new(false, RegistrationStatus.ValidationRejected, error);
        _endpoints ??= _options.RegistrationEndpoints.ToArray();
        var factory = _serviceProvider.GetRequiredService<IRegistrationEndpointFactory>();
        RegistrationResult? last = null;
        for (var attempt = 0; attempt < _endpoints.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _endpointClient ??= factory.Create(_endpoints[_endpointIndex]);
            try
            {
                // Preflight is read-only. An unavailable endpoint cannot acquire ownership here.
                var capabilities = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
                if (!capabilities.Capabilities.Contains("instance-leases"))
                    return new(false, RegistrationStatus.ValidationRejected, "Ordered registration requires instance leases on every endpoint.");
                var previouslyOwned = _endpointMayOwnRegistration;
                _endpointMayOwnRegistration = true; // Cancellation and lost replies retain uncertainty.
                last = await RegisterCoreAsync(ct).ConfigureAwait(false);
                if (last.Success) return last;
                _endpointMayOwnRegistration = previouslyOwned || last.MayHaveApplied;
                if (last.Status is not (RegistrationStatus.Unavailable or RegistrationStatus.DeadlineExceeded or RegistrationStatus.StorageFailed)) return last;
            }
            catch (RpcException ex) { last = RpcResult(ex, ct); }
            if (last.Status is not (RegistrationStatus.Unavailable or RegistrationStatus.DeadlineExceeded or RegistrationStatus.StorageFailed)) return last;
            if (_endpointMayOwnRegistration)
            {
                // A lease timeout alone cannot prove that an in-flight renewal did not commit later.
                var released = await UnregisterCoreAsync(ct).ConfigureAwait(false);
                if (!released.Success && released.Status != RegistrationStatus.NotFound)
                    return new(false, RegistrationStatus.Unavailable, "The previous gateway may still own this registration. Failover awaits confirmed release.");
                ClearEndpointOwnership();
            }
            _endpointIndex = (_endpointIndex + 1) % _endpoints.Length;
            Volatile.Write(ref _endpointClient, null);
        }
        return last!;
    }

    private void ClearEndpointOwnership()
    {
        _endpointMayOwnRegistration = false;
        Volatile.Write(ref _lease, null);
        Interlocked.Exchange(ref _registrationVersion, -1);
        ObserveRegistration(false, "Released");
    }

    internal static string? ValidateEndpoints(GatewayRegistrationOptions options)
    {
        if (options.RegistrationEndpoints.Count == 0) return null;
        if (options.RegistrationEndpoints.Count > 8 || options.UseGrpcRegistration != true || string.IsNullOrWhiteSpace(options.InstanceId)
            || options.LeaseDurationSeconds is < 15 or > 3600 || options.HeartbeatEnabled == false)
            return "Ordered registration requires 1–8 gRPC endpoints, InstanceId, heartbeats, and a 15–3600 second lease.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in options.RegistrationEndpoints)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
                || !seen.Add(uri.AbsoluteUri)) return "Registration endpoints must be distinct absolute HTTP(S) origins without credentials, paths, queries, or fragments.";
        }
        return null;
    }
}
