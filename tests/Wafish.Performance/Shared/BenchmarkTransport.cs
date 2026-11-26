using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Wafish.Performance;

internal sealed class BenchmarkTransport : IDisposable
{
    public const string CertificateEnvironment = "WAFISH_BENCHMARK_CERTIFICATE";
    private X509Certificate2? _certificate;
    public string Mode { get; }
    public string? CertificatePath { get; }
    public bool Tls => Mode is "https1" or "https2";
    public Version Version => Mode is "h2c" or "https2" ? HttpVersion.Version20 : HttpVersion.Version11;
    public string Scheme => Tls ? "https" : "http";
    public string Description => $"HTTP/{Version} exact {(Tls ? "TLS" : "cleartext")}";
    public string Protocol => Version.Major == 2 ? "HTTP/2" : "HTTP/1.1";
    public BenchmarkTransport(string mode, string? certificatePath = null)
    {
        if (mode is not ("http1" or "h2c" or "https1" or "https2"))
            throw new ArgumentException("--transport must be http1, h2c, https1, or https2.");
        Mode = mode;
        CertificatePath = certificatePath;
    }
    public static BenchmarkTransport Read(string[] args) => new(BenchmarkOptions.Read(args, "--transport", "http1"), Environment.GetEnvironmentVariable(CertificateEnvironment));
    public Uri Address(string value, string name) => BenchmarkOptions.LoopbackAddress(value, name, Scheme);
    public X509Certificate2 Certificate => _certificate ??= X509CertificateLoader.LoadPkcs12FromFile(
        CertificatePath ?? throw new ArgumentException("TLS hosts require WAFISH_BENCHMARK_CERTIFICATE pointing to a local PFX."), null,
        X509KeyStorageFlags.EphemeralKeySet);
    public void ConfigureHandler(SocketsHttpHandler handler)
    {
        handler.UseProxy = false;
        handler.AllowAutoRedirect = false;
        handler.EnableMultipleHttp2Connections = true;
        if (Tls && CertificatePath is not null)
        {
            var expected = Certificate.GetCertHash(HashAlgorithmName.SHA256);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
    }
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler();
        ConfigureHandler(handler);
        return new HttpClient(handler) { DefaultRequestVersion = Version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }
    public void ConfigureRequest(HttpRequestMessage request)
    {
        request.Version = Version;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    }
    public void VerifyResponse(HttpResponseMessage response, bool upstream = false)
    {
        if (response.Version != Version) throw new InvalidOperationException("Response protocol differs from the requested benchmark transport.");
        if (upstream && (!response.Headers.TryGetValues("X-Benchmark-Protocol", out var values) || values.Single() != Protocol))
            throw new InvalidOperationException("Upstream protocol differs from the requested benchmark transport.");
    }
    public static void CreateCertificate(string path)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public void Dispose() => _certificate?.Dispose();
}
