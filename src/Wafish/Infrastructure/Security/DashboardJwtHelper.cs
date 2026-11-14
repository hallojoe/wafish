using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>JWT token generation and validation (zero external dependency).</summary>
public static class DashboardJwtHelper
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Generate a signed JWT token.</summary>
    /// <param name="username">Subject claim.</param>
    /// <param name="secret">HMAC-SHA256 signing key.</param>
    public static string GenerateToken(string username, string secret)
        => GenerateToken(username, secret, TimeProvider.System);

    /// <summary>Generate an eight-hour token using the supplied clock.</summary>
    public static string GenerateToken(string username, string secret, TimeProvider clock)
    {
        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }, _jsonOptions);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            sub = username,
            jti = Guid.NewGuid().ToString("N"),
            iss = "Wafish.Dashboard",
            iat = now,
            exp = now + 28800  // 8 hours
        }, _jsonOptions);

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        var signature = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            .ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = Base64UrlEncode(signature);

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    /// <summary>Validate a signed JWT token.</summary>
    /// <param name="token">The JWT string.</param>
    /// <param name="secret">HMAC-SHA256 signing key.</param>
    /// <returns>(valid, username) tuple.</returns>
    public static (bool Valid, string? Username) ValidateToken(string token, string secret)
        => ValidateToken(token, secret, TimeProvider.System);

    /// <summary>Validate a token using the supplied clock.</summary>
    public static (bool Valid, string? Username) ValidateToken(string token, string secret, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(token) || token.Length > 16384)
        {
            return (false, null);
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return (false, null);
        }

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expectedSig = ComputeSignature(signingInput, secret);

        byte[] actualSig;
        byte[] payloadBytes;
        try
        {
            actualSig = Base64UrlDecode(parts[2]);
            payloadBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            // Malformed base64url payload/signature — treat as invalid, never throw.
            return (false, null);
        }

        if (!ConstantTimeEquals(actualSig, expectedSig))
        {
            return (false, null);
        }

        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "HS256" ||
                header.RootElement.GetProperty("typ").GetString() != "JWT")
            {
                return (false, null);
            }

            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var exp) ||
                exp <= clock.GetUtcNow().ToUnixTimeSeconds())
            {
                return (false, null);
            }

            if (!root.TryGetProperty("iss", out var issuer) || issuer.GetString() != "Wafish.Dashboard")
            {
                return (false, null);
            }

            var username = root.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
            return (!string.IsNullOrWhiteSpace(username), username);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Invalid or unexpected payload structure — treat as invalid, never throw.
            return (false, null);
        }
    }

    private static byte[] ComputeSignature(string input, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        var pad = b64.Length % 4;
        if (pad == 2)
        {
            b64 += "==";
        }
        else if (pad == 3)
        {
            b64 += "=";
        }

        return Convert.FromBase64String(b64);
    }

    /// <summary>Constant-time comparison to prevent timing attacks.</summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
