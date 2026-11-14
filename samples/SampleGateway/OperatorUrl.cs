using Wafish.Dashboard.Infrastructure.Deployment;

namespace SampleGateway;

internal static class OperatorUrl
{
    public static string? Resolve(IEnumerable<string> addresses, IEnumerable<EndpointRoleMap> endpoints, string prefix)
    {
        var roles = endpoints.ToArray();
        var candidates = addresses.Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Scheme is "http" or "https").Cast<Uri>()
            .Where(uri => roles.Length == 0 || roles.Any(role => (role.Port == uri.Port || role.Port == 0)
                && !role.EndpointName.Contains("grpc", StringComparison.OrdinalIgnoreCase)
                && (role.Role.Equals("Dashboard", StringComparison.OrdinalIgnoreCase) || role.Role.Equals("All", StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(uri => uri.Scheme == "https");
        var endpoint = candidates.FirstOrDefault();
        if (endpoint is null) return null;
        var result = new UriBuilder(endpoint) { Path = "/" + prefix.Trim('/') + "/", Query = "", Fragment = "" };
        if (result.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+") result.Host = "localhost";
        return result.Uri.AbsoluteUri;
    }
}
