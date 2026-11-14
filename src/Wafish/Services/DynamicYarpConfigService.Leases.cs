using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Wafish.Models;
using Wafish.Services.ProxyConfigHealth;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>Lease timing negotiated by the gateway. Tokens are never included in stored configuration.</summary>
public sealed record InstanceRegistrationLease(DateTimeOffset ExpiresAt, int RenewalSeconds, int GraceSeconds, bool Draining);
public sealed record ConfigurationRevisions(long ActiveVersion, long PersistedVersion);
public sealed record RegistrationLeaseResult(RouteOperationResult Outcome, InstanceRegistrationLease? Lease);

public partial class DynamicYarpConfigService
{
    private readonly TimeProvider _leaseClock = TimeProvider.System;
    private const string LeaseHash = "wafish.registration.lease-hash";
    private const string LeaseExpiry = "wafish.registration.lease-expiry";
    private const string LeaseDuration = "wafish.registration.lease-duration";
    internal const string LeaseDrain = "wafish.registration.draining";
    private const int LeaseGraceSeconds = 30;
    private sealed record LeaseRegistration(string Token, int Seconds);

    public DynamicYarpConfigService(WafishProxyConfigProvider configProvider, IConfigurationSnapshotRepository snapshots,
        IConfigChangeAuditLog auditLog, ILoggerFactory loggerFactory, IGatewaySnapshotCompiler snapshotCompiler,
        IGatewaySnapshotPublisher snapshotPublisher, IConfigValidator configValidator, IProxyConfigErrorStore errorStore, TimeProvider clock)
        : this(configProvider, snapshots, auditLog, loggerFactory, snapshotCompiler, snapshotPublisher, configValidator, errorStore) => _leaseClock = clock;

    public async Task<RegistrationLeaseResult> RegisterLeasedInstanceAsync(string serviceId, string instanceId,
        ClusterConfig cluster, IReadOnlyList<RouteConfig> routes, string leaseToken, int durationSeconds = 60, CancellationToken cancellationToken = default)
    {
        if (leaseToken is null || leaseToken.Length is < 32 or > 128 || !leaseToken.All(char.IsAsciiHexDigit) || durationSeconds is < 15 or > 3600)
            return new(new(false, "A lease requires a 32–128 digit hexadecimal credential and a duration of 15–3600 seconds."), null);
        var outcome = await RegisterInstanceCoreAsync(serviceId, instanceId, cluster, routes, new(leaseToken, durationSeconds), cancellationToken);
        return new(outcome, outcome.Success ? ReadLease(serviceId, instanceId) : null);
    }

    /// <summary>Returns revision numbers without copying configuration or secrets.</summary>
    public async Task<ConfigurationRevisions> GetRevisionsAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try { return new(_state.Version, _state.PersistedVersion); }
        finally { _semaphore.Release(); }
    }

    public bool HasLeasedService(string serviceId) => _state.Config.Clusters.SelectMany(c => c.Config.Destinations?.Values ?? [])
        .Any(d => OwnedBy(d.Metadata, serviceId) && HasLease(d));

    public bool IsLeasedInstance(string serviceId, string instanceId) => _state.Config.Clusters.SelectMany(c => c.Config.Destinations?.Values ?? [])
        .Any(d => OwnedBy(d.Metadata, serviceId, instanceId) && HasLease(d));

    public async Task<RegistrationLeaseResult> RenewInstanceLeaseAsync(string serviceId, string instanceId, string token,
        bool drain = false, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var mutation = BeginMutation(cancellationToken);
            var cluster = mutation.Config.Clusters.FirstOrDefault(c => c.Config.Destinations?.Values.Any(d => OwnedBy(d.Metadata, serviceId, instanceId)) == true);
            var owned = cluster?.Config.Destinations?.Values.FirstOrDefault(d => OwnedBy(d.Metadata, serviceId, instanceId));
            if (owned is null || !HasLease(owned) || !LeaseMatches(owned, token) || LeaseExpired(owned))
                return new(new(false, "The instance lease is absent, expired, or owned by another registration."), null);
            var seconds = int.Parse(owned.Metadata![LeaseDuration], CultureInfo.InvariantCulture);
            var metadata = CreateLeaseMetadata(new(token, seconds));
            var draining = drain || IsDraining(owned);
            if (draining) metadata[LeaseDrain] = "true";
            cluster!.Config = cluster.Config with { Destinations = cluster.Config.Destinations!.ToDictionary(p => p.Key,
                p => OwnedBy(p.Value.Metadata, serviceId, instanceId) ? p.Value with { Metadata = WithOwner(metadata, serviceId, instanceId) } : p.Value) };
            if (drain) _auditLog.RecordSuccess("DrainInstance", serviceId, instanceId);
            var outcome = await mutation.CommitAsync(drain ? "DrainInstance" : "RenewInstanceLease", serviceId);
            return new(outcome, outcome.Success ? ToLease(metadata) : null);
        }
        finally { _semaphore.Release(); }
    }

    /// <summary>Removes expired leased destinations atomically. Legacy registrations are unaffected.</summary>
    public async Task<RouteOperationResult> ExpireInstanceLeasesAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!_state.Config.Clusters.Any(c => c.Config.Destinations?.Values.Any(d => HasLease(d) && LeaseExpired(d)) == true))
                return new(true, "No leases expired.") { ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion };
            using var mutation = BeginMutation(cancellationToken);
            var removed = 0;
            foreach (var cluster in mutation.Config.Clusters.Where(c => c.Source == "grpc").ToArray())
            {
                var destinations = cluster.Config.Destinations;
                if (destinations is null) continue;
                var remaining = destinations.Where(p => !HasLease(p.Value) || !LeaseExpired(p.Value)).ToDictionary(p => p.Key, p => p.Value);
                removed += destinations.Count - remaining.Count;
                if (remaining.Count == destinations.Count) continue;
                cluster.Config = cluster.Config with { Destinations = remaining };
                if (remaining.Count == 0)
                {
                    mutation.Config.Routes.RemoveAll(r => r.Source == "grpc" && r.Config.ClusterId == cluster.Config.ClusterId);
                    if (!mutation.Config.Routes.Any(r => r.Config.ClusterId == cluster.Config.ClusterId)) mutation.Config.Clusters.Remove(cluster);
                }
            }
            if (removed == 0) return new(true, "No leases expired.") { ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion };
            _auditLog.RecordSuccess("ExpireInstanceLeases", "registration", after: new { removed });
            return await mutation.CommitAsync("ExpireInstanceLeases");
        }
        finally { _semaphore.Release(); }
    }

    private Dictionary<string, string> CreateLeaseMetadata(LeaseRegistration lease) => new()
    {
        [LeaseHash] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Token))),
        [LeaseExpiry] = _leaseClock.GetUtcNow().AddSeconds(lease.Seconds).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        [LeaseDuration] = lease.Seconds.ToString(CultureInfo.InvariantCulture)
    };
    private static Dictionary<string, string> WithOwner(IReadOnlyDictionary<string, string>? metadata, string service, string instance)
    {
        var result = metadata?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        result[RegistrationServiceKey] = service; result[RegistrationInstanceKey] = instance;
        return result;
    }
    internal static bool HasLease(DestinationConfig destination) => destination.Metadata?.ContainsKey(LeaseHash) == true;
    internal static bool IsDraining(DestinationConfig destination) => destination.Metadata?.GetValueOrDefault(LeaseDrain) == "true";
    private bool LeaseExpired(DestinationConfig destination) => !long.TryParse(destination.Metadata?.GetValueOrDefault(LeaseExpiry), CultureInfo.InvariantCulture, out var expiry)
        || _leaseClock.GetUtcNow().ToUnixTimeMilliseconds() - LeaseGraceSeconds * 1000L >= expiry;
    private static bool LeaseMatches(DestinationConfig destination, string? token)
    {
        if (token is null || token.Length is < 32 or > 128) return false;
        var expected = destination.Metadata?.GetValueOrDefault(LeaseHash) ?? "";
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }
    private InstanceRegistrationLease? ReadLease(string service, string instance)
    {
        var owned = _state.Config.Clusters.SelectMany(c => c.Config.Destinations?.Values ?? []).FirstOrDefault(d => OwnedBy(d.Metadata, service, instance) && HasLease(d));
        return owned is null ? null : ToLease(owned.Metadata!);
    }
    private static InstanceRegistrationLease ToLease(IReadOnlyDictionary<string, string> metadata) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(metadata[LeaseExpiry], CultureInfo.InvariantCulture)),
        Math.Max(5, int.Parse(metadata[LeaseDuration], CultureInfo.InvariantCulture) / 3), LeaseGraceSeconds,
        metadata.GetValueOrDefault(LeaseDrain) == "true");
}
