using System.Security.Cryptography;
using System.Text;
using Wafish.Models;
using Wafish.Dashboard.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
namespace Wafish.Services;

/// <summary>Shared credential checks for REST, gRPC, and administration.</summary>
public static class ControlPlaneCredentials
{
    public static bool Equal(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && actual != null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(actual)));

    public static bool Validate(IHeaderDictionary headers, GatewayApiAuthOptions options)
    {
        var authorization = headers.Authorization.Count == 1 ? headers.Authorization[0] : null;
        return options.Mode switch
        {
            GatewayApiAuthMode.None => true,
            GatewayApiAuthMode.ApiKey => headers.TryGetValue(options.ApiKeyHeaderName, out var values) && values.Count == 1 && Equal(options.ApiKey, values[0]),
            GatewayApiAuthMode.BasicAuth => ValidateBasic(authorization, options.Username, options.Password),
            GatewayApiAuthMode.Jwt => authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true &&
                DashboardJwtHelper.ValidateToken(authorization[7..], options.JwtSecret ?? "").Valid,
            _ => false
        };
    }

    public static bool ValidateBasic(string? authorization, string? username, string? password)
    {
        if (authorization?.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
            var separator = decoded.IndexOf(':');
            return separator > 0 && Equal(username, decoded[..separator]) && Equal(password, decoded[(separator + 1)..]);
        }
        catch (FormatException) { return false; }
    }
}
