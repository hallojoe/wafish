using System.Net;
using WafIsh.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
namespace WafIsh.Services;

/// <summary>Network restrictions shared by every administration transport.</summary>
public sealed class ControlPlaneAccessPolicy(IOptions<ControlPlaneSecurityOptions> options)
{
    public void ValidateStartup(bool anonymous)
    {
        if (anonymous && !options.Value.AllowAnonymousLoopback)
            throw new InvalidOperationException("Administration requires authentication. For direct local development only, set Gateway:Security:ControlPlane:AllowAnonymousLoopback=true.");
    }

    public bool IsAllowed(HttpContext context, bool anonymous)
    {
        var request = context.Request;
        var directLoopback = IsLoopback(context.Connection.RemoteIpAddress) &&
            IsLoopback(context.Connection.LocalIpAddress) && IsLoopbackHost(request.Host.Host) &&
            !request.Headers.Keys.Any(h => h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                h.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) || h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
        if (anonymous && (!options.Value.AllowAnonymousLoopback || !directLoopback)) return false;
        if (!directLoopback && !request.IsHttps) return false;
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0 ||
                !string.Equals(origin.GetLeftPart(UriPartial.Authority), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsLoopback(IPAddress? address)
        => address != null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopback(address));
}
