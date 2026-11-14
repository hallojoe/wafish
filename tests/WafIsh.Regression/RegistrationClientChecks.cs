using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WafIsh.Extensions;
using WafIsh.Services;

internal static class RegistrationClientChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Registration: gRPC preserves paths, instance identity, destinations, and credentials", async () =>
        {
            using var logs = new CaptureLogs();
            var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddWafIshClient(o =>
            {
                o.GatewayUrl = "http://gateway.test"; o.GrpcUrl = "http://gateway.test:5001";
                o.UseGrpcRegistration = true; o.AutoResolveIp = false; o.RouteName = "orders"; o.ClusterName = "shared";
                o.InstanceId = "instance-one"; o.MatchPaths = [" /first ", "second", " "];
                o.DestinationAddress = "http://localhost:9000/"; o.ExtraDestinationAddresses = [" http://localhost:9001/ "];
                o.ApiKey = "test-key"; o.HeartbeatIntervalSeconds = 4000;
            });
            var grpc = new CaptureGrpcClient();
            services.AddSingleton<WafIsh.GatewayRegistry.GatewayRegistry.GatewayRegistryClient>(grpc);
            await using var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "GatewayRegistrationHostedService");
            var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
            await hosted.StartAsync(default);
            Check(grpc.Registration is { ServiceId: "orders", ServiceName: "shared", InstanceId: "instance-one" }, "gRPC identities changed.");
            Check(grpc.Registration!.Paths.SequenceEqual(["/first", "/second"]) && grpc.Registration.Destinations.Count == 2, "gRPC paths or destinations were lost.");
            Check(grpc.Headers!.Any(h => h.Key == "x-api-key" && h.Value == "test-key"), "gRPC credentials were lost.");
            Check(await client.HeartbeatAsync() && grpc.Heartbeat?.InstanceId == "instance-one", "Heartbeat lost instance identity.");
            await hosted.StopAsync(default);
            Check(grpc.Unregistration?.InstanceId == "instance-one", "Shutdown lost instance identity.");
            Check(logs.Messages.Any(m => m.Contains("exceeds maximum", StringComparison.Ordinal)), "Invalid heartbeat interval was not clamped.");
        });

        await run("Registration: HTTP payloads, credentials, failures, heartbeats, and shutdown", async () =>
        {
            using var handler = new CaptureHandler();
            using var logs = new CaptureLogs();
            var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddWafIshClient(o =>
            {
                o.GatewayUrl = "http://gateway.test"; o.RouteName = "orders"; o.ClusterName = "orders-cluster";
                o.MatchPath = "orders/{**rest}"; o.DestinationAddress = "http://localhost:9000/";
                o.AutoResolveIp = false; o.UseIpIsolation = false; o.ApiKey = "test-key";
                o.Transforms = [new() { ["PathRemovePrefix"] = "/orders" }];
                o.HeartbeatIntervalSeconds = 1;
            });
            services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler));
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
            var hosted = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "GatewayRegistrationHostedService");
            await hosted.StartAsync(default);
            var registration = handler.Calls.Single();
            using var payload = JsonDocument.Parse(registration.Body!);
            Check(registration.Path == "/api/gateway/register-route" && registration.Key == "test-key", "Registration path or credentials changed.");
            Check(payload.RootElement.GetProperty("matchPath").GetString() == "/orders/{**rest}", "Match path was not normalized.");
            Check(payload.RootElement.GetProperty("transforms")[0].GetProperty("PathRemovePrefix").GetString() == "/orders", "Transforms were lost.");
            Check(await client.HeartbeatAsync(), "Successful heartbeat was rejected.");
            Check(handler.Calls.Last().Path.EndsWith("/orders/heartbeat", StringComparison.Ordinal), "Wrong heartbeat route.");
            handler.Status = HttpStatusCode.ServiceUnavailable;
            Check(!await client.RegisterAsync() && !await client.HeartbeatAsync() && !await client.UnregisterAsync(), "Gateway failures reported success.");
            handler.Status = HttpStatusCode.NotFound;
            Check(await client.UnregisterAsync(), "Already removed route must be harmless.");
            handler.Status = HttpStatusCode.OK;
            await hosted.StopAsync(default);
            Check(handler.Calls.Last().Method == HttpMethod.Delete && handler.Calls.Last().Path == "/api/gateway/orders", "Shutdown did not unregister.");
            Check(logs.Messages.Any(m => m.Contains("Transforms:", StringComparison.Ordinal)) && logs.Messages.Any(m => m.Contains("Heartbeat started", StringComparison.Ordinal)), "Enabled diagnostic messages were lost.");
        });
    }

    private sealed class CaptureGrpcClient : WafIsh.GatewayRegistry.GatewayRegistry.GatewayRegistryClient
    {
        public WafIsh.GatewayRegistry.RegisterServiceRequest? Registration { get; private set; }
        public WafIsh.GatewayRegistry.UnregisterServiceRequest? Unregistration { get; private set; }
        public WafIsh.GatewayRegistry.HeartbeatRequest? Heartbeat { get; private set; }
        public Grpc.Core.Metadata? Headers { get; private set; }
        public override Grpc.Core.AsyncUnaryCall<WafIsh.GatewayRegistry.RegisterServiceResponse> RegisterServiceAsync(WafIsh.GatewayRegistry.RegisterServiceRequest request, Grpc.Core.CallOptions options)
        {
            Registration = request; Headers = options.Headers;
            return Reply(new WafIsh.GatewayRegistry.RegisterServiceResponse { Success = true });
        }
        public override Grpc.Core.AsyncUnaryCall<WafIsh.GatewayRegistry.UnregisterServiceResponse> UnregisterServiceAsync(WafIsh.GatewayRegistry.UnregisterServiceRequest request, Grpc.Core.CallOptions options)
        {
            Unregistration = request;
            return Reply(new WafIsh.GatewayRegistry.UnregisterServiceResponse { Success = true });
        }
        public override Grpc.Core.AsyncUnaryCall<WafIsh.GatewayRegistry.HeartbeatResponse> HeartbeatAsync(WafIsh.GatewayRegistry.HeartbeatRequest request, Grpc.Core.CallOptions options)
        {
            Heartbeat = request;
            return Reply(new WafIsh.GatewayRegistry.HeartbeatResponse { Success = true });
        }
        private static Grpc.Core.AsyncUnaryCall<T> Reply<T>(T response) => new(Task.FromResult(response), Task.FromResult(new Grpc.Core.Metadata()), () => Grpc.Core.Status.DefaultSuccess, () => new Grpc.Core.Metadata(), () => { });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<(HttpMethod Method, string Path, string? Key, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new(Status) { Content = new StringContent("{\"success\":true}") };
        }
    }
    private sealed class CaptureLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(List<string> messages) : ILogger
        {
            public bool IsEnabled(LogLevel level) => level != LogLevel.None;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => messages.Add(format(state, error));
        }
    }
}
