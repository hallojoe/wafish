using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Wafish.ReactUi;

/// <summary>Embedded asset identity and supported administration contract. Contains no host configuration.</summary>
public sealed record ReactUiBuildMetadata(string PackageVersion, string AssetSha256, int AdministrationContractVersion);

public static class ReactUiBuildInfo
{
    private static readonly Lazy<ReactUiBuildMetadata> Metadata = new(Create);
    public static ReactUiBuildMetadata Current => Metadata.Value;

    private static ReactUiBuildMetadata Create()
    {
        var assembly = typeof(ReactUiBuildInfo).Assembly;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ReactUi/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var normalized = resource.Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(normalized + "\0" + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return new(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown",
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), 1);
    }
}
