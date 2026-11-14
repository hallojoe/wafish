using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wafish.Extensions;
using Wafish.GatewayRegistry;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class ClientTlsChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Security: registration client rejects an untrusted TLS certificate", async () =>
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(certificate); }));
            await using var app = builder.Build();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddWafIshClient(o => { o.GatewayUrl = address; o.GrpcUrl = address; });
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<GatewayRegistry.GatewayRegistryClient>();
            try { await client.GetServicesAsync(new GetServicesRequest(), deadline: DateTime.UtcNow.AddSeconds(5)); throw new Exception("Untrusted certificate accepted."); }
            catch (RpcException ex) when (ex.ToString().Contains("certificate", StringComparison.OrdinalIgnoreCase)) { }
            await app.StopAsync();
        });
    }
}
