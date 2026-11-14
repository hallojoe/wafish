using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Extensions;
using Wafish.GatewayRegistry;
using Wafish.Services;

internal static class NeedSevenClientChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Client leases and sample readiness: negotiation, loss, expiry, recovery, and drain preserve liveness", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            await using var gateway = await ConfigurationTransactionChecks.Fixture.Create(configure: s => s.AddSingleton<TimeProvider>(clock));
            var rpc = new Bridge(new(gateway.Config, NullLogger<GatewayRegistryGrpcService>.Instance));
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sample:RegistrationReadiness"] = "true" });
            builder.AddDefaultHealthChecks();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddWafishClient(o =>
            {
                o.GatewayUrl = "http://gateway.test"; o.UseGrpcRegistration = true; o.RouteName = "lease";
                o.InstanceId = "one"; o.DestinationAddress = "http://localhost:9000/"; o.AutoResolveIp = false; o.LeaseDurationSeconds = 15;
            });
            builder.Services.AddSingleton<GatewayRegistry.GatewayRegistryClient>(rpc);
            await using var services = builder.Services.BuildServiceProvider();
            var client = services.GetRequiredService<GatewayAutoRegistrationClient>();
            var status = services.GetRequiredService<IRegistrationStatusSource>();
            var health = services.GetRequiredService<HealthCheckService>();
            Check(!status.GetRegistrationStatus().Ready && (await health.CheckHealthAsync()).Status == HealthStatus.Unhealthy, "Initial readiness must fail.");
            Check((await health.CheckHealthAsync(r => r.Tags.Contains("live"))).Status == HealthStatus.Healthy, "Registration changed liveness.");
            rpc.Unsupported = true;
            Check(!(await client.RegisterAsync()) && rpc.Registrations == 0, "Unsupported lease silently downgraded.");
            rpc.Unsupported = false;
            Check(await client.RegisterAsync() && status.GetRegistrationStatus().Ready, "Registration did not become ready.");
            Check((await health.CheckHealthAsync()).Status == HealthStatus.Healthy, "Sample readiness failed after registration.");
            Check(await client.UpdateDestinationsAsync(["http://localhost:9001/"]), "Leased destination update failed.");
            clock.Advance(TimeSpan.FromSeconds(10));
            Check(await client.HeartbeatAsync() && status.GetRegistrationStatus().LastSuccessAt == clock.GetUtcNow(), "Lease refresh failed.");
            rpc.FailHeartbeat = true;
            Check(!await client.HeartbeatAsync() && status.GetRegistrationStatus().Ready, "Transient loss discarded the lease grace.");
            clock.Advance(TimeSpan.FromSeconds(45));
            Check(!status.GetRegistrationStatus().Ready, "Expired lease stayed ready.");
            Check((await health.CheckHealthAsync(r => r.Tags.Contains("live"))).Status == HealthStatus.Healthy, "Lease loss failed liveness.");
            rpc.FailHeartbeat = false;
            Check(!await client.HeartbeatAsync(), "Expired lease revived through renewal.");
            Check(await client.RegisterAsync() && status.GetRegistrationStatus().Ready, "Registration did not recover.");
            Check(await client.DrainAsync() && status.GetRegistrationStatus().Draining && !status.GetRegistrationStatus().Ready, "Drain did not fail readiness.");
            Check(!await client.RegisterAsync(), "A draining client resumed registration.");
            Check(await client.UnregisterAsync() && !status.GetRegistrationStatus().Registered, "Unregister retained registration state.");
            Check(!await client.DrainAsync(), "Missing lease accepted drain.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await new RegistrationReadinessHealthCheck(services).CheckHealthAsync(new(), cancel.Token); throw new Exception("Cancelled readiness succeeded."); } catch (OperationCanceledException) { }
        });
    }
    private sealed class Bridge(GatewayRegistryGrpcService server) : GatewayRegistry.GatewayRegistryClient
    {
        public bool Unsupported { get; set; }
        public bool FailHeartbeat { get; set; }
        public int Registrations { get; private set; }
        public override AsyncUnaryCall<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CallOptions options)
            => Reply(Unsupported ? Task.FromResult(new CapabilitiesResponse()) : server.GetCapabilities(request, Context()));
        public override AsyncUnaryCall<RegisterServiceResponse> RegisterServiceAsync(RegisterServiceRequest request, CallOptions options)
        { Registrations++; return Reply(server.RegisterService(request, Context())); }
        public override AsyncUnaryCall<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CallOptions options)
            => Reply(FailHeartbeat ? Task.FromResult(new HeartbeatResponse { Success = false }) : server.Heartbeat(request, Context()));
        public override AsyncUnaryCall<UnregisterServiceResponse> UnregisterServiceAsync(UnregisterServiceRequest request, CallOptions options)
            => Reply(server.UnregisterService(request, Context()));
        public override AsyncUnaryCall<UpdateDestinationsResponse> UpdateDestinationsAsync(UpdateDestinationsRequest request, CallOptions options)
            => Reply(server.UpdateDestinations(request, Context()));
        private static ServerCallContext Context() => new SecurityChecks.CallContext(new());
        private static AsyncUnaryCall<T> Reply<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
