using System.Security.Claims;
using Wafish.Models;

namespace Wafish.Plugin.RateLimit.Redis;

internal static class RedisTenantIdentity
{
    internal static int Resolve(ClaimsPrincipal principal, RedisTenantPartitionPolicy policy, out string? key)
    {
        key = null;
        var authenticated = false;
        string? tenant = null;
        var identities = 0; var claims = 0;
        foreach (var identity in principal.Identities)
        {
            if (++identities > 16) return 403;
            authenticated |= identity.IsAuthenticated;
            if (!identity.IsAuthenticated || !string.Equals(identity.AuthenticationType, policy.AuthenticationType, StringComparison.Ordinal)) continue;
            foreach (var claim in identity.Claims)
            {
                if (++claims > 256) return 403;
                if (!string.Equals(claim.Type, policy.ClaimType, StringComparison.Ordinal)) continue;
                if (tenant is not null || claim.Issuer != policy.ClaimIssuer || claim.OriginalIssuer != policy.ClaimIssuer
                    || claim.ValueType != ClaimValueTypes.String || claim.Value.Length is < 1 or > 128) return 403;
                tenant = claim.Value;
            }
        }
        if (tenant is not null && policy.TenantKeys.TryGetValue(tenant, out key)) return 0;
        return authenticated ? 403 : 401;
    }
}
