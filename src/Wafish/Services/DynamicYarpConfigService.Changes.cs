using System.Security.Cryptography;
using System.Text.Json;
using Wafish.Models;
using Wafish.Storage;

namespace Wafish.Services;

public partial class DynamicYarpConfigService : IConfigurationChangeService
{
    private readonly IConfigurationSnapshotRepository _snapshotRepository;

    public async Task<ConfigurationPreviewResult> PreviewConfigurationAsync(ConfigurationChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            try
            {
                request = JsonSerializer.Deserialize<ConfigurationChangeRequest>(JsonSerializer.Serialize(request))!;
                CheckChangeVersion(request);
                var candidate = BuildCandidate(request);
                _ = await _publisher.PreparePreviewAsync(candidate, candidate.Version, cancellationToken);
                var normalized = new ConfigurationChangeRequest(request.ExpectedVersion, candidate.Routes.Select(r => r.Config).ToArray(), candidate.Clusters.Select(c => c.Config).ToArray())
                { RouteActivation = candidate.Routes.ToDictionary(r => r.Config.RouteId, r => r.Enabled) };
                var changes = Diff(_state.Config.Routes.Select(r => (r.Config.RouteId, Fingerprint(new { r.Config, r.Enabled }))),
                    candidate.Routes.Select(r => (r.Config.RouteId, Fingerprint(new { r.Config, r.Enabled }))), "route")
                    .Concat(Diff(_state.Config.Clusters.Select(c => (c.Config.ClusterId, Fingerprint(c.Config))),
                        candidate.Clusters.Select(c => (c.Config.ClusterId, Fingerprint(c.Config))), "cluster")).ToArray();
                return new(true, request.ExpectedVersion, _state.Version, _state.PersistedVersion, normalized, changes, []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ConfigurationVersionConflictException)
            {
                return new(false, request.ExpectedVersion, _state.Version, _state.PersistedVersion, null, [], [error.Message]);
            }
        }
        finally { _semaphore.Release(); }
    }

    public async Task<RouteOperationResult> ApplyConfigurationAsync(ConfigurationChangeRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl))
            return ChangeFailure(ConfigurationUpdateStatus.ValidationRejected, "An idempotency key of 1–128 printable characters is required.");
        if (_snapshotRepository is not IIdempotentConfigurationSnapshotRepository receipts)
            return ChangeFailure(ConfigurationUpdateStatus.ValidationRejected, "The storage provider does not support atomic configuration receipts.");
        ArgumentNullException.ThrowIfNull(request);
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Freeze caller-owned collections before fingerprinting, validation, and persistence.
            request = JsonSerializer.Deserialize<ConfigurationChangeRequest>(JsonSerializer.Serialize(request))!;
            var fingerprint = Fingerprint(request);
            if (await receipts.GetReceiptAsync(idempotencyKey, cancellationToken) is { } previous) return Replay(previous, fingerprint);
            GatewayDynamicConfig candidate;
            Action activate;
            try
            {
                CheckChangeVersion(request);
                candidate = BuildCandidate(request);
                activate = await _publisher.PrepareAsync(candidate, candidate.Version, cancellationToken);
            }
            catch (ConfigurationVersionConflictException error) { return ChangeFailure(ConfigurationUpdateStatus.VersionConflict, error.Message); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { return ChangeFailure(ConfigurationUpdateStatus.ValidationRejected, error.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Retain commit ownership after this point, even when the request disconnects.
                await receipts.SaveWithReceiptAsync(DynamicConfigPersister.ToSnapshot(candidate), request.ExpectedVersion, idempotencyKey, fingerprint, CancellationToken.None);
            }
            catch (ConfigurationReceiptExistsException)
            {
                var receipt = await receipts.GetReceiptAsync(idempotencyKey, CancellationToken.None);
                return receipt is null ? ChangeFailure(ConfigurationUpdateStatus.VersionConflict, "The receipt expired. Inspect the current revision before retrying.") : Replay(receipt, fingerprint);
            }
            catch (ConfigurationVersionConflictException error) { return ChangeFailure(ConfigurationUpdateStatus.VersionConflict, error.Message) with { PersistedVersion = error.ActualVersion }; }
            catch (Exception error)
            {
                _state.LogFailure?.Invoke(error, "ApplyConfiguration");
                return ChangeFailure(ConfigurationUpdateStatus.StorageFailed, "Configuration storage failed. No candidate was activated.");
            }
            _state.PersistedVersion = candidate.Version;
            RouteOperationResult result;
            try
            {
                activate();
                _state.Config = candidate;
                result = new(true, "Configuration applied") { ActiveVersion = candidate.Version, PersistedVersion = candidate.Version };
            }
            catch (Exception error)
            {
                _state.LogFailure?.Invoke(error, "ApplyConfiguration");
                result = ChangeFailure(ConfigurationUpdateStatus.ActivationFailed, "Configuration was stored but activation failed. Restart to reconcile the stored revision.");
            }
            if (result.Success)
            {
                try { _auditLog.RecordSuccess("ApplyConfiguration", "full configuration"); }
                catch (Exception error) { _state.LogFailure?.Invoke(error, "AuditConfigurationChange"); }
            }
            try { await receipts.CompleteReceiptAsync(idempotencyKey, JsonSerializer.Serialize(result), CancellationToken.None); }
            catch (Exception error)
            {
                _state.LogFailure?.Invoke(error, "CompleteConfigurationReceipt");
                return PendingReceipt(candidate.Version);
            }
            return result;
        }
        finally { _semaphore.Release(); }
    }

    private void CheckChangeVersion(ConfigurationChangeRequest request)
    {
        if (request.ExpectedVersion != _state.PersistedVersion) throw new ConfigurationVersionConflictException(request.ExpectedVersion, _state.PersistedVersion);
        if (_state.Version != _state.PersistedVersion) throw new InvalidOperationException("A stored revision is not active. Restart before another change.");
    }

    private GatewayDynamicConfig BuildCandidate(ConfigurationChangeRequest request)
    {
        if (request.Routes is null || request.Clusters is null || request.Routes.Count > 10000 || request.Clusters.Count > 10000)
            throw new ArgumentException("Supply route and cluster lists with at most 10,000 entries each.");
        if (request.Routes.Any(r => r is null || string.IsNullOrWhiteSpace(r.RouteId)) || request.Clusters.Any(c => c is null || string.IsNullOrWhiteSpace(c.ClusterId)))
            throw new ArgumentException("Every route and cluster requires an ID.");
        if (request.Routes.Select(r => r.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Routes.Count ||
            request.Clusters.Select(c => c.ClusterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Clusters.Count)
            throw new ArgumentException("Route and cluster IDs must be unique without case differences.");
        if (request.RouteActivation?.Keys.Any(id => !request.Routes.Any(r => r.RouteId == id)) == true)
            throw new ArgumentException("Route activation contains an unknown route ID.");
        var existing = DynamicConfigState.Clone(_state.Config);
        var candidate = new GatewayDynamicConfig { Version = checked(request.ExpectedVersion + 1), LastModified = DateTime.UtcNow };
        foreach (var cluster in request.Clusters)
        {
            var item = existing.Clusters.FirstOrDefault(c => string.Equals(c.Config.ClusterId, cluster.ClusterId, StringComparison.OrdinalIgnoreCase)) ?? new DynamicClusterConfig();
            item.Config = cluster; candidate.Clusters.Add(item);
        }
        foreach (var route in request.Routes)
        {
            var item = existing.Routes.FirstOrDefault(r => string.Equals(r.Config.RouteId, route.RouteId, StringComparison.OrdinalIgnoreCase)) ?? new DynamicRouteConfig();
            item.Config = DynamicYarpConfigHelpers.NormalizeTransforms(route);
            item.ClusterUid = DynamicConfigState.ResolveClusterUid(candidate, route.ClusterId);
            if (request.RouteActivation?.TryGetValue(route.RouteId, out var enabled) == true) item.Enabled = enabled;
            candidate.Routes.Add(item);
        }
        return DynamicConfigState.Clone(candidate);
    }

    private RouteOperationResult ChangeFailure(ConfigurationUpdateStatus status, string message)
        => new(false, message) { Status = status, ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion };
    private static RouteOperationResult PendingReceipt(long version) => new(false, "Configuration was committed; its original activation acknowledgement is unavailable. Inspect the gateway before further changes.")
        { Status = ConfigurationUpdateStatus.ActivationFailed, PersistedVersion = version, ActiveVersion = null };
    private RouteOperationResult Replay(ConfigurationMutationReceipt receipt, string fingerprint) => receipt.Fingerprint != fingerprint
        ? ChangeFailure(ConfigurationUpdateStatus.VersionConflict, "The idempotency key was already used for a different payload.")
        : receipt.ResultJson is null ? PendingReceipt(receipt.Version) : JsonSerializer.Deserialize<RouteOperationResult>(receipt.ResultJson);

    private static IEnumerable<ConfigurationChange> Diff(IEnumerable<(string Id, string Hash)> before, IEnumerable<(string Id, string Hash)> after, string kind)
    {
        var previous = before.ToDictionary(p => p.Id, p => p.Hash, StringComparer.OrdinalIgnoreCase);
        var next = after.ToDictionary(p => p.Id, p => p.Hash, StringComparer.OrdinalIgnoreCase);
        foreach (var id in previous.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            if (!previous.TryGetValue(id, out var old)) yield return new(kind, id, "Added");
            else if (!next.TryGetValue(id, out var current)) yield return new(kind, id, "Removed");
            else if (old != current) yield return new(kind, id, "Updated");
    }

    private static string Fingerprint<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
            else element.WriteTo(writer);
        }
    }
}
