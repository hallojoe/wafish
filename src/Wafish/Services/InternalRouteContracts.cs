using Microsoft.AspNetCore.Http;

namespace Wafish.Services;

public sealed record InternalRouteCall(string RouteUid, string Path, string QueryString = "", IReadOnlyList<string>? ForwardHeaders = null);
public sealed record InternalRouteResponse(int StatusCode, string? ContentType, string? ContentEncoding, string? RetryAfter, byte[] Body);
public sealed class InternalRouteException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
/// <summary>A prepared batch owns its contexts and a runtime lease until all child calls finish.</summary>
public interface IInternalRouteBatch : IAsyncDisposable
{
    long SnapshotVersion { get; }
    Task<InternalRouteResponse> ExecuteAsync(int index, CancellationToken cancellationToken);
}
public interface IInternalRouteExecutor
{
    Task<IInternalRouteBatch> PrepareAsync(HttpContext parent, IReadOnlyList<InternalRouteCall> calls, int maxResponseBytes, CancellationToken cancellationToken);
}
/// <summary>Validates cross-route relationships before a candidate snapshot is persisted or activated.</summary>
public interface IGatewaySnapshotValidator
{
    void Validate(GatewaySnapshot snapshot);
}
