using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Wafish.Performance;

internal static class BenchmarkServerTransport
{
    public static void ConfigureBenchmarkListener(this IWebHostBuilder host, Uri address, BenchmarkTransport transport)
    {
        var ip = address.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(address.Host.Trim('[', ']'));
        if (!IPAddress.IsLoopback(ip)) throw new ArgumentException("Benchmark listeners must use loopback.");
        var certificate = transport.Tls ? transport.Certificate : null;
        host.ConfigureKestrel(options => options.Listen(ip, address.Port, listener =>
        {
            listener.Protocols = transport.Version.Major == 2 ? HttpProtocols.Http2 : HttpProtocols.Http1;
            if (certificate is not null) listener.UseHttps(certificate);
        }));
    }
}
