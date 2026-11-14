using System.Text;
using WafIsh.Models;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WafIsh.Services;

/// <summary>
/// gRPC server-side auth interceptor. Validates credentials against <see cref="GatewayApiAuthOptions"/>.
/// Enforces the configured ApiKey or BasicAuth mode.
/// Skips validation when auth is not configured (mode=None).
/// </summary>
internal class GrpcAuthInterceptor : Interceptor
{
    private readonly GatewayApiAuthOptions _options;
    private readonly ILogger<GrpcAuthInterceptor> _logger;
    private readonly ControlPlaneAccessPolicy _policy;

    public GrpcAuthInterceptor(
        IOptions<GatewayApiAuthOptions> options,
        ILogger<GrpcAuthInterceptor> logger,
        ControlPlaneAccessPolicy policy)
    {
        _options = options.Value;
        _logger = logger;
        _policy = policy;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (!_policy.IsAllowed(context.GetHttpContext(), _options.Mode == GatewayApiAuthMode.None))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Administration requires HTTPS or an allowed direct loopback connection."));

        if (_options.Mode == GatewayApiAuthMode.None)
        {
            return await continuation(request, context);
        }

        if (!ValidateAuth(context))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing credentials"));
        }

        return await continuation(request, context);
    }

    private bool ValidateAuth(ServerCallContext context)
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary();
        foreach (var entry in context.RequestHeaders.Where(e => !e.IsBinary))
        {
            headers.TryGetValue(entry.Key, out var values);
            headers[entry.Key] = Microsoft.Extensions.Primitives.StringValues.Concat(values, entry.Value);
        }
        return ControlPlaneCredentials.Validate(headers, _options);
    }
}
