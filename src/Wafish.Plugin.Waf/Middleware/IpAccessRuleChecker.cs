using System.Net;
using Wafish.Infrastructure;
using Wafish.Plugin.Waf.Helpers;

namespace Wafish.Plugin.Waf;

/// <summary>
/// Checks client IP against whitelist/blacklist rules.
/// </summary>
public sealed class IpAccessRuleChecker : IWafRuleChecker
{
    public string RuleId => "ip-access";
    public static readonly IpAccessRuleChecker Instance = new();

    public WafCheckResult Check(WafCheckContext context)
    {
        if (!context.Options.EnableIpCheck)
        {
            return WafCheckResult.Passed;
        }

        var clientIp = ClientIpResolver.GetClientIp(context.HttpContext);
        if (string.IsNullOrEmpty(clientIp))
        {
            return WafCheckResult.Passed;
        }

        var opts = context.Options;

        if (opts.IpWhitelist.Count > 0)
        {
            if (!opts.IpWhitelist.Any(ip => IpMatcher.Matches(ip.Trim(), clientIp)))
            {
                return WafCheckResult.Blocked("IpBlocked");
            }

            return WafCheckResult.Passed;
        }

        if (opts.IpBlacklist.Count > 0)
        {
            if (opts.IpBlacklist.Any(ip => IpMatcher.Matches(ip.Trim(), clientIp)))
            {
                return WafCheckResult.Blocked("IpBlocked");
            }
        }

        return WafCheckResult.Passed;
    }
}
