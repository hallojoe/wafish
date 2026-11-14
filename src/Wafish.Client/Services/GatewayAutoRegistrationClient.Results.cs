using System.Text.Json;
using Grpc.Core;
using Wafish.Models;

namespace Wafish.Services;

public partial class GatewayAutoRegistrationClient
{
    private static RegistrationResult RpcResult(RpcException failure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = failure.StatusCode switch
        {
            StatusCode.InvalidArgument => RegistrationStatus.ValidationRejected,
            StatusCode.Unauthenticated => RegistrationStatus.Unauthenticated,
            StatusCode.PermissionDenied => RegistrationStatus.PermissionDenied,
            StatusCode.NotFound => RegistrationStatus.NotFound,
            StatusCode.Aborted or StatusCode.AlreadyExists => RegistrationStatus.VersionConflict,
            StatusCode.Unavailable => RegistrationStatus.Unavailable,
            StatusCode.DeadlineExceeded => RegistrationStatus.DeadlineExceeded,
            StatusCode.Internal => RegistrationStatus.InternalError,
            _ => RegistrationStatus.Unknown
        };
        return new(false, status, $"Gateway RPC failed: {failure.StatusCode}.", RemoteStatus: failure.StatusCode.ToString());
    }

    private static async Task<RegistrationResult> ReadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = (int)response.StatusCode switch
        {
            >= 200 and < 300 => RegistrationStatus.Applied,
            400 or 422 => RegistrationStatus.ValidationRejected,
            401 => RegistrationStatus.Unauthenticated,
            403 => RegistrationStatus.PermissionDenied,
            404 => RegistrationStatus.NotFound,
            409 => RegistrationStatus.VersionConflict,
            503 => RegistrationStatus.Unavailable,
            504 => RegistrationStatus.DeadlineExceeded,
            _ => RegistrationStatus.Unknown
        };
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new(response.IsSuccessStatusCode, fallback, $"Gateway returned HTTP {(int)response.StatusCode}.");
        try
        {
            using var document = JsonDocument.Parse(text);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) return new(false, RegistrationStatus.Unknown, "Gateway returned an invalid operation response.");
            string? Value(string key) => body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long? Revision(string key) => body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) ? revision : null;
            var success = response.IsSuccessStatusCode && (!body.TryGetProperty("success", out var flag) || flag.ValueKind == JsonValueKind.True);
            var status = Value("outcome") ?? Value("status");
            // Transport errors without an explicit application outcome must remain uncertain.
            return RegistrationResult.Remote(success, Value("message") ?? Value("detail") ?? $"Gateway returned HTTP {(int)response.StatusCode}.",
                status ?? (success || !response.IsSuccessStatusCode ? fallback.ToString() : null), Revision("persistedVersion"), Revision("activeVersion"));
        }
        catch (JsonException)
        {
            return new(false, response.IsSuccessStatusCode ? RegistrationStatus.Unknown : fallback, "Gateway returned an unreadable operation response.");
        }
    }
}
