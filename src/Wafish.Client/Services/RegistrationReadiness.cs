using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Wafish.GatewayRegistry;
using Wafish.Models;

namespace Wafish.Services;

/// <summary>Local registration state. Health probes read it without contacting the gateway.</summary>
public sealed record RegistrationReadiness(bool Registered, bool Draining, bool Ready, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? LastSuccessAt, string Status);

public interface IRegistrationStatusSource
{
    RegistrationReadiness GetRegistrationStatus();
}

public partial class GatewayAutoRegistrationClient : IRegistrationStatusSource
{
    private readonly string _leaseToken = RandomNumberGenerator.GetHexString(64);
    private RegistrationLease? _lease;
    private RegistrationReadiness _readiness = new(false, false, false, null, null, "NotRegistered");
    private TimeProvider RegistrationClock => _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    internal int? LeaseRenewalSeconds => Volatile.Read(ref _lease)?.RenewalSeconds;

    public RegistrationReadiness GetRegistrationStatus()
    {
        var state = Volatile.Read(ref _readiness);
        var lease = Volatile.Read(ref _lease);
        var valid = lease is null || RegistrationClock.GetUtcNow().ToUnixTimeMilliseconds() < lease.ExpiresAt + lease.GraceSeconds * 1000L;
        return state with { Ready = state.Registered && !state.Draining && valid };
    }

    public async Task<RegistrationResult> RegisterResultAsync(CancellationToken ct = default)
    {
        await _registrationOperations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await RegisterOrderedAsync(ct).ConfigureAwait(false);
            ObserveRegistration(result.Success, result.Success ? "Registered" : "RegistrationFailed");
            return result;
        }
        finally { _registrationOperations.Release(); }
    }

    public async Task<RegistrationResult> UnregisterResultAsync(CancellationToken ct = default)
    {
        await _registrationOperations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.RegistrationEndpoints.Count > 0 && !_endpointMayOwnRegistration)
                return new(false, RegistrationStatus.NotFound, "No endpoint owns this registration.");
            Volatile.Write(ref _readiness, _readiness with { Registered = false, Ready = false, Draining = true, Status = "Unregistering" });
            var result = await UnregisterCoreAsync(ct).ConfigureAwait(false);
            if (result.Success || result.Status == RegistrationStatus.NotFound) ClearEndpointOwnership();
            return result;
        }
        finally { _registrationOperations.Release(); }
    }

    public async Task<bool> HeartbeatAsync(CancellationToken ct = default)
    {
        await _registrationOperations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.RegistrationEndpoints.Count > 0 && !_endpointMayOwnRegistration) return false;
            var success = await HeartbeatCoreAsync(ct).ConfigureAwait(false);
            ObserveRegistration(success, success ? "Registered" : "HeartbeatFailed");
            return success;
        }
        finally { _registrationOperations.Release(); }
    }

    /// <summary>Stops new routing to this leased instance. Existing forwarded requests may finish.</summary>
    public async Task<bool> DrainAsync(CancellationToken ct = default)
    {
        await _registrationOperations.WaitAsync(ct).ConfigureAwait(false);
        try { return await DrainCoreAsync(ct).ConfigureAwait(false); }
        finally { _registrationOperations.Release(); }
    }

    private async Task<bool> DrainCoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _lease) is null) return false;
        try
        {
            var response = await GrpcClient.HeartbeatAsync(new HeartbeatRequest { ServiceId = RegistrationOptionsResolver.GetRouteName(_options),
                InstanceId = _options.InstanceId ?? "", LeaseToken = _leaseToken, Drain = true }, BuildGrpcCallOptions(ct)).ConfigureAwait(false);
            if (response.Success)
            {
                Volatile.Write(ref _lease, response.Lease?.Clone());
                ObserveRegistration(true, "Draining");
            }
            return response.Success;
        }
        catch (RpcException) { ct.ThrowIfCancellationRequested(); return false; }
    }

    private void ObserveRegistration(bool success, string status)
    {
        var previous = Volatile.Read(ref _readiness);
        var lease = Volatile.Read(ref _lease);
        Volatile.Write(ref _readiness, new(success || lease is not null && previous.Registered,
            lease?.Draining == true, false, lease is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(lease.ExpiresAt),
            success ? RegistrationClock.GetUtcNow() : previous.LastSuccessAt, status));
    }
}
