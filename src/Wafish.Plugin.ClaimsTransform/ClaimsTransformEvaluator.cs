using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using System.Diagnostics.Metrics;

namespace Wafish.Plugin.ClaimsTransform;

public sealed record ResolvedClaimMapping(ClaimTarget Target, string Name, string? Value);
public sealed record ClaimsTransformResult(int? ErrorStatus, string? Path, ImmutableArray<ResolvedClaimMapping> Mappings);

public static class ClaimsTransformEvaluator
{
    private static readonly Meter Meter = new("Wafish.ClaimsTransform");
    private static readonly Counter<long> Evaluations = Meter.CreateCounter<long>("wafish.claims_transform.evaluations");
    public static ClaimsTransformResult Evaluate(CompiledClaimsTransform config, ClaimsPrincipal principal)
    {
        Evaluations.Add(1);
        var identities = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        if (identities.Length == 0) return new(401, null, []);
        var mappings = ImmutableArray.CreateBuilder<ResolvedClaimMapping>();
        var path = config.PathTemplate;
        var bytes = 0;
        foreach (var mapping in config.Mappings)
        {
            var values = identities.SelectMany(i => i.Claims).Where(c => c.Type == mapping.Claim).Select(c => c.Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (values.Length > 1 || values.Length == 0 && mapping.Required) return new(403, null, []);
            var value = values.FirstOrDefault();
            if (value != null)
            {
                var size = Encoding.UTF8.GetByteCount(value);
                bytes += size;
                if (size > 4096 || bytes > 16384 || value.Any(char.IsControl) || mapping.Target == ClaimTarget.Path &&
                    (value.Length == 0 || value is "." or ".." || value.IndexOfAny(['/', '\\', '%']) >= 0)) return new(403, null, []);
            }
            if (mapping.Target == ClaimTarget.Path) path = path!.Replace("{" + mapping.Name + "}", Uri.EscapeDataString(value!), StringComparison.Ordinal);
            else mappings.Add(new(mapping.Target, mapping.Name, value));
        }
        if (path?.Length > 16384) return new(403, null, []);
        return new(null, path, mappings.ToImmutable());
    }
}
