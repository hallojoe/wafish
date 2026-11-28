using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Wafish.Dashboard.Infrastructure.Auth;

// This value is set only after administration authentication succeeds. Actor labels are not identity proofs.
internal static class DashboardUserIdentity
{
    private static readonly object Key = new();
    public static string? Owner(HttpContext context) => context.Items[Key] as string;
    public static void Clear(HttpContext context) => context.Items.Remove(Key);
    public static void Account(HttpContext context, string scheme, string? subject)
    {
        if (Valid(subject)) Set(context, ["account-v1", scheme, subject!]);
    }
    public static void Principal(HttpContext context)
    {
        var identities = context.User.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (identities.Length != 1 || !Valid(identities[0].AuthenticationType)) return;
        var subjects = identities[0].Claims.Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub").ToArray();
        if (subjects.Length != 1 || !Valid(subjects[0].Value) || !Valid(subjects[0].Issuer) || !Valid(subjects[0].OriginalIssuer)) return;
        Set(context, ["principal-v1", identities[0].AuthenticationType!, subjects[0].Type, subjects[0].Issuer, subjects[0].OriginalIssuer, subjects[0].Value]);
    }
    private static bool Valid(string? value) => value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static void Set(HttpContext context, string[] parts) =>
        context.Items[Key] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
