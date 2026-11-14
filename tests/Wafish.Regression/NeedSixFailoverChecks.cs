using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wafish.Extensions;
using Wafish.GatewayRegistry;
using Wafish.Models;
using Wafish.Services;

internal static class NeedSixFailoverChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Registration failover: ordered preflight skips unavailable gateways and keeps one stable owner", async () =>
        {
            var first = new Endpoint { Unavailable = true }; var second = new Endpoint();
            await using var root = Create(first, second);
            var client = root.GetRequiredService<GatewayAutoRegistrationClient>();
            Check(await client.RegisterAsync() && client.RegistrationEndpoint == "https://second.test", "Ordered preflight did not select the fallback.");
            first.Unavailable = false;
            Check(await client.RegisterAsync() && await client.HeartbeatAsync() && await client.UpdateDestinationsAsync(["https://backend.test/"]), "Selected owner operations failed.");
            Check(first.Registrations == 0 && second.Registrations == 2 && second.Instance == "stable-one", "Client failed back unexpectedly or changed identity.");
            Check(await client.UnregisterAsync() && !second.Owned && !client.GetRegistrationStatus().Registered, "Unregister did not reach the selected owner.");
            Check(!await client.HeartbeatAsync(), "Unregistered client renewed a different endpoint.");
        });
        await run("Registration failover: ambiguous registration cannot reach another gateway before confirmed release", async () =>
        {
            var first = new Endpoint { LoseRegistrationReply = true, RefuseRelease = true }; var second = new Endpoint();
            await using var root = Create(first, second);
            var client = root.GetRequiredService<GatewayAutoRegistrationClient>();
            var result = await client.RegisterResultAsync();
            Check(!result.Success && result.MayHaveApplied && first.Owned && second.Registrations == 0, "Ambiguous registration created a second owner.");
            first.Unavailable = true;
            Check(!await client.RegisterAsync() && second.Registrations == 0, "Unreachable owner was abandoned.");
            first.RefuseRelease = false;
            Check(await client.RegisterAsync() && !first.Owned && second.Owned, "Confirmed release did not permit failover.");
            Check(first.Instance == second.Instance && first.Token == second.Token, "Failover changed instance or lease identity.");
        });
        await run("Registration failover: cancellation retains ownership and concurrent mutations remain serialized", async () =>
        {
            var first = new Endpoint(); var second = new Endpoint();
            await using var root = Create(first, second);
            var client = root.GetRequiredService<GatewayAutoRegistrationClient>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.WaitForCancellation = entered;
            using var cancelled = new CancellationTokenSource();
            var pending = client.RegisterResultAsync(cancelled.Token); await entered.Task;
            cancelled.Cancel();
            try { await pending; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            first.Unavailable = true; first.RefuseRelease = true;
            Check(!await client.RegisterAsync() && second.Registrations == 0, "Cancelled mutation discarded possible ownership.");
            first.WaitForCancellation = null; first.RefuseRelease = false;
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.RegisterAsync()));
            Check(results.All(r => r) && !first.Owned && second.Owned, "Concurrent retry created conflicting registrations.");
        });
        await run("Registration failover: invalid endpoint lists reject startup and disabled registration sends nothing", async () =>
        {
            foreach (var endpoints in new[] { new[] { "https://first.test", "https://first.test/" }, new[] { "https://user:secret@first.test" }, new[] { "file:///tmp/endpoint" }, Enumerable.Range(0, 9).Select(i => $"https://host{i}.test").ToArray() })
            {
                await using var root = Create(new(), new(), options => options.RegistrationEndpoints = endpoints.ToList());
                try { root.GetRequiredService<GatewayAutoRegistrationClient>(); throw new Exception("Invalid endpoints accepted."); }
                catch (Microsoft.Extensions.Options.OptionsValidationException) { }
            }
            var first = new Endpoint(); var second = new Endpoint();
            await using var disabled = Create(first, second, o => o.Enabled = false);
            Check(!(await disabled.GetRequiredService<GatewayAutoRegistrationClient>().RegisterAsync()) && first.Preflights == 0, "Disabled registration made network calls.");
        });
    }
    private static ServiceProvider Create(Endpoint first, Endpoint second, Action<GatewayRegistrationOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWafishClient(o =>
        {
            o.RegistrationEndpoints = ["https://first.test", "https://second.test"]; o.UseGrpcRegistration = true;
            o.InstanceId = "stable-one"; o.RouteName = "service"; o.LeaseDurationSeconds = 30;
            o.DestinationAddress = "https://backend.test/"; o.AutoResolveIp = false; configure?.Invoke(o);
        });
        builder.Services.AddSingleton<IRegistrationEndpointFactory>(new Factory(first, second));
        return builder.Services.BuildServiceProvider();
    }
    private sealed class Factory(Endpoint first, Endpoint second) : IRegistrationEndpointFactory
    {
        public GatewayRegistry.GatewayRegistryClient Create(string endpoint) => endpoint.Contains("first") ? first : second;
    }
    private sealed class Endpoint : GatewayRegistry.GatewayRegistryClient
    {
        public bool Unavailable, RefuseRelease, LoseRegistrationReply, Owned;
        public int Registrations, Preflights;
        public string? Instance, Token;
        public TaskCompletionSource? WaitForCancellation;
        public override AsyncUnaryCall<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CallOptions options)
        {
            Preflights++;
            return Reply(Unavailable ? Task.FromException<CapabilitiesResponse>(Failure()) : Task.FromResult(new CapabilitiesResponse { Capabilities = { "instance-leases" } }));
        }
        public override AsyncUnaryCall<RegisterServiceResponse> RegisterServiceAsync(RegisterServiceRequest request, CallOptions options)
        {
            Registrations++; Owned = true; Instance = request.InstanceId; Token = request.LeaseToken;
            return Reply(Register());
            async Task<RegisterServiceResponse> Register()
            {
                if (WaitForCancellation is { } entered) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, options.CancellationToken); }
                if (LoseRegistrationReply) throw Failure();
                return new() { Success = true, Status = "Applied", Lease = Lease() };
            }
        }
        public override AsyncUnaryCall<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CallOptions options)
            => Reply(Task.FromResult(new HeartbeatResponse { Success = Owned, Lease = Lease() }));
        public override AsyncUnaryCall<UnregisterServiceResponse> UnregisterServiceAsync(UnregisterServiceRequest request, CallOptions options)
        {
            if (RefuseRelease) return Reply(Task.FromException<UnregisterServiceResponse>(Failure()));
            Check(request.LeaseToken == Token && request.InstanceId == Instance, "Cleanup omitted uncertain ownership identity.");
            Owned = false;
            return Reply(Task.FromResult(new UnregisterServiceResponse { Success = true, Status = "Applied" }));
        }
        public override AsyncUnaryCall<UpdateDestinationsResponse> UpdateDestinationsAsync(UpdateDestinationsRequest request, CallOptions options)
            => Reply(Task.FromResult(new UpdateDestinationsResponse { Success = Owned, Status = "Applied" }));
        private static RegistrationLease Lease() => new() { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(), GraceSeconds = 30, RenewalSeconds = 10 };
        private static RpcException Failure() => new(new Status(StatusCode.Unavailable, "unavailable"));
        private static AsyncUnaryCall<T> Reply<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
