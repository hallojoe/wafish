using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace WafIsh.Dashboard.Infrastructure.Auth;

/// <summary>Loads one signing key for all control-plane transports.</summary>
public sealed class JwtSecretProvider
{
    private readonly string _filePath;
    private readonly object _sync = new();
    private string? _cachedSecret;

    public JwtSecretProvider(IWebHostEnvironment env, ILogger<JwtSecretProvider> logger)
        => _filePath = Path.Combine(env.ContentRootPath, "Data", ".jwt-secret");

    public string GetSecret(string? configuredSecret)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(configuredSecret))
            {
                return Validate(configuredSecret);
            }

            return _cachedSecret ??= LoadOrCreateSecret();
        }
    }

    private string LoadOrCreateSecret()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        if (File.Exists(_filePath))
        {
            return ReadSecret();
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, new { secret, generatedAt = DateTime.UtcNow });
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, _filePath); }
            catch (IOException) when (File.Exists(_filePath)) { return ReadSecret(); }
            return secret;
        }
        finally { if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string ReadSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
        return Validate(document.RootElement.GetProperty("secret").GetString());
    }

    private static string Validate(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException("The JWT signing key must contain at least 32 bytes.");
        }

        return secret;
    }
}
