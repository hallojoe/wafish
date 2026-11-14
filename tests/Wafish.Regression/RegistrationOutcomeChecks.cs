using System.Net;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Extensions;
using Wafish.GatewayRegistry;
using Wafish.Models;
using Wafish.Services;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class RegistrationOutcomeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Registration capabilities: discovery, legacy fallback, authentication, and cancellation remain distinct", async () =>
        {
            using var handler = new ReplyHandler(); var rpc = new ReplyClient();
            await using var provider = Provider(true, rpc, handler);
            var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
            Check((await client.GetCapabilitiesAsync()).ProtocolVersion == 1, "Capability response lost version.");
            rpc.Failure = new RpcException(new Status(StatusCode.Unimplemented, "legacy"));
            Check((await client.GetCapabilitiesAsync()).ProtocolVersion == 0, "Legacy fallback failed.");
            rpc.Failure = null;
            Check(await client.RegisterAsync(), "Legacy discovery changed registration.");
            rpc.Failure = new RpcException(new Status(StatusCode.Unauthenticated, "auth"));
            try { await client.GetCapabilitiesAsync(); throw new Exception("Authentication failure became legacy fallback."); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await client.GetCapabilitiesAsync(cancellation.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            var server = new GatewayRegistryGrpcService(null!, NullLogger<GatewayRegistryGrpcService>.Instance);
            var result = await server.GetCapabilities(new(), new SecurityChecks.CallContext(new()));
            Check(result.ProtocolVersion == 1 && result.Capabilities.Contains("expected-version"), "Server contract missing.");
        });
        await run("Registration outcomes: HTTP and gRPC preserve application statuses and revisions", CheckApplicationAsync);
        await run("Registration outcomes: transport failures retain uncertainty and credential classification", CheckTransportAsync);
        await run("Registration outcomes: caller cancellation propagates and disabled operations stay local", CheckCancellationAsync);
        await run("Registration outcomes: wire omissions and malformed responses do not invent confirmed revisions", CheckLegacyAsync);
        await run("Registration protocol: validation, conflicts, storage, activation, and internal errors remain distinct", CheckServerAsync);
    }

    private static ServiceProvider Provider(bool grpc, ReplyClient rpc, ReplyHandler http, Action<GatewayRegistrationOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWafishClient(options =>
        {
            options.GatewayUrl = "http://gateway.test"; options.GrpcUrl = "http://gateway.test:5001";
            options.UseGrpcRegistration = grpc; options.AutoResolveIp = false; options.RouteName = "fixture";
            options.DestinationAddress = "http://127.0.0.1:9000/"; options.InstanceId = grpc ? "fixture-instance" : null;
            options.ApiKey = "fixture-secret";
            configure?.Invoke(options);
        });
        services.AddSingleton<GatewayRegistry.GatewayRegistryClient>(rpc);
        services.AddSingleton<IHttpClientFactory>(new Factory(http));
        return services.BuildServiceProvider();
    }

    private static async Task CheckApplicationAsync()
    {
        foreach (var grpc in new[] { false, true })
        {
            using var handler = new ReplyHandler(); var rpc = new ReplyClient();
            await using var provider = Provider(grpc, rpc, handler);
            var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
            foreach (var (status, code, advice, applied) in new[]
            {
                (RegistrationStatus.Applied, 200, RegistrationRetryAdvice.None, true),
                (RegistrationStatus.ValidationRejected, 400, RegistrationRetryAdvice.CorrectRequest, false),
                (RegistrationStatus.VersionConflict, 409, RegistrationRetryAdvice.RefreshRevision, false),
                (RegistrationStatus.StorageFailed, 503, RegistrationRetryAdvice.Retry, false),
                (RegistrationStatus.ActivationFailed, 500, RegistrationRetryAdvice.InspectBeforeRetry, true),
                (RegistrationStatus.NotFound, 404, RegistrationRetryAdvice.CorrectRequest, false)
            })
            {
                var success = status == RegistrationStatus.Applied;
                rpc.Status = status.ToString(); rpc.Success = success; rpc.Stored = 11; rpc.Active = 10;
                handler.Code = code; handler.Body = JsonSerializer.Serialize(new { success, outcome = status.ToString(), status = code,
                    message = "fixture", persistedVersion = 11, activeVersion = 10 });
                var results = new List<RegistrationResult> { await client.RegisterResultAsync(), await client.UnregisterResultAsync() };
                if (grpc) results.Add(await client.UpdateDestinationsResultAsync(["http://127.0.0.1:9001/"]));
                foreach (var result in results)
                    Check(result.Status == status && result.Success == success && result.PersistedVersion == 11 && result.ActiveVersion == 10
                        && result.RetryAdvice == advice && result.MayHaveApplied == applied, "Application result lost semantics: " + status);
            }
            Check(!await client.RegisterAsync(), "Boolean registration wrapper accepted a rejected mutation.");
            Check(await client.UnregisterAsync(), "Boolean unregistration wrapper lost missing-route compatibility.");
            if (grpc) Check(!await client.UpdateDestinationsAsync(["http://127.0.0.1:9001/"]), "Boolean update wrapper accepted failure.");
        }
    }

    private static async Task CheckTransportAsync()
    {
        using var handler = new ReplyHandler(); var rpc = new ReplyClient();
        await using var provider = Provider(true, rpc, handler);
        var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
        foreach (var (code, expected) in new[]
        {
            (StatusCode.InvalidArgument, RegistrationStatus.ValidationRejected), (StatusCode.Unauthenticated, RegistrationStatus.Unauthenticated),
            (StatusCode.PermissionDenied, RegistrationStatus.PermissionDenied), (StatusCode.NotFound, RegistrationStatus.NotFound),
            (StatusCode.Aborted, RegistrationStatus.VersionConflict), (StatusCode.AlreadyExists, RegistrationStatus.VersionConflict),
            (StatusCode.Unavailable, RegistrationStatus.Unavailable), (StatusCode.DeadlineExceeded, RegistrationStatus.DeadlineExceeded),
            (StatusCode.Internal, RegistrationStatus.InternalError), (StatusCode.Cancelled, RegistrationStatus.Unknown)
        })
        {
            rpc.Failure = new RpcException(new Status(code, "fixture-secret"));
            foreach (var result in new[] { await client.RegisterResultAsync(), await client.UnregisterResultAsync(),
                await client.UpdateDestinationsResultAsync(["http://localhost:9001/"]) })
                Check(result.Status == expected && result.PersistedVersion == null && result.ActiveVersion == null
                    && !result.Message.Contains("fixture-secret"), "RPC error mapping leaked details or lost status.");
        }
        await using var httpProvider = Provider(false, new ReplyClient(), handler);
        var httpClient = httpProvider.GetRequiredService<GatewayAutoRegistrationClient>();
        foreach (var (code, expected) in new[] { (400, RegistrationStatus.ValidationRejected), (401, RegistrationStatus.Unauthenticated),
            (403, RegistrationStatus.PermissionDenied), (404, RegistrationStatus.NotFound), (409, RegistrationStatus.VersionConflict),
            (503, RegistrationStatus.Unavailable), (504, RegistrationStatus.DeadlineExceeded), (500, RegistrationStatus.Unknown) })
        {
            handler.Code = code; handler.Body = "";
            Check((await httpClient.RegisterResultAsync()).Status == expected && (await httpClient.UnregisterResultAsync()).Status == expected,
                "HTTP transport mapping changed.");
        }
        foreach (var failure in new Exception[] { new HttpRequestException("fixture-secret"), new TaskCanceledException("fixture-secret") })
        {
            handler.Failure = failure;
            foreach (var result in new[] { await httpClient.RegisterResultAsync(), await httpClient.UnregisterResultAsync() })
                Check(result.MayHaveApplied && result.RetryAdvice == RegistrationRetryAdvice.InspectBeforeRetry && !result.Message.Contains("fixture-secret"),
                    "Uncertain transport failure suggested a safe replay.");
        }
    }

    private static async Task CheckCancellationAsync()
    {
        foreach (var grpc in new[] { false, true })
        {
            using var handler = new ReplyHandler { Block = true }; var rpc = new ReplyClient { Block = true };
            await using var provider = Provider(grpc, rpc, handler);
            var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
            var operations = new List<Func<CancellationToken, Task<RegistrationResult>>> { client.RegisterResultAsync, client.UnregisterResultAsync };
            if (grpc) operations.Add(ct => client.UpdateDestinationsResultAsync(["http://localhost:9001/"], ct));
            foreach (var operation in operations)
            {
                using var cancellation = new CancellationTokenSource();
                rpc.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); handler.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = operation(cancellation.Token);
                await (grpc ? rpc.Entered.Task : handler.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                try { await pending; throw new Exception("Caller cancellation became a remote failure."); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await client.RegisterAsync(cancelled.Token); throw new Exception("Boolean wrapper swallowed cancellation."); }
            catch (OperationCanceledException) { }
        }
        using var localHandler = new ReplyHandler();
        await using var disabled = Provider(false, new ReplyClient(), localHandler, options => options.GatewayUrl = "");
        var local = disabled.GetRequiredService<GatewayAutoRegistrationClient>();
        Check((await local.RegisterResultAsync()).Status == RegistrationStatus.Disabled && (await local.UnregisterResultAsync()).Status == RegistrationStatus.Disabled,
            "Disabled registration contacted the gateway.");
        Check((await local.UpdateDestinationsResultAsync([])).Status == RegistrationStatus.Disabled, "HTTP destination replacement became supported implicitly.");
        await using var invalid = Provider(true, new ReplyClient(), localHandler);
        Check((await invalid.GetRequiredService<GatewayAutoRegistrationClient>().UpdateDestinationsResultAsync([])).Status == RegistrationStatus.ValidationRejected,
            "Empty destinations reached the transport.");
    }

    private static async Task CheckLegacyAsync()
    {
        using var handler = new ReplyHandler(); var rpc = new ReplyClient { Status = "", Success = true };
        await using var provider = Provider(true, rpc, handler);
        var client = provider.GetRequiredService<GatewayAutoRegistrationClient>();
        var legacy = await client.RegisterResultAsync();
        Check(legacy.Success && legacy.Status == RegistrationStatus.Applied && legacy.ActiveVersion == null, "Old response acquired invented revisions.");
        rpc.Success = false;
        Check((await client.RegisterResultAsync()).Status == RegistrationStatus.Unknown, "Old failure acquired an invented application outcome.");
        rpc.Status = "FutureOutcome";
        var future = await client.RegisterResultAsync();
        Check(future.Status == RegistrationStatus.Unknown && future.RemoteStatus == "FutureOutcome" && future.MayHaveApplied, "Unknown remote outcome was discarded.");
        rpc.Status = "StorageFailed"; rpc.Success = true;
        Check((await client.RegisterResultAsync()).Status == RegistrationStatus.Unknown, "Contradictory response claimed a successful mutation.");
        await using var httpProvider = Provider(false, new ReplyClient(), handler);
        var http = httpProvider.GetRequiredService<GatewayAutoRegistrationClient>();
        foreach (var body in new[] { "{", "[]", "{\"success\":false}" })
        {
            handler.Body = body;
            var result = await http.RegisterResultAsync();
            Check(result.Status == RegistrationStatus.Unknown && !result.Success && result.MayHaveApplied, "Malformed success response was trusted.");
        }
        handler.Body = "{\"success\":true,\"status\":\"Applied\",\"activeVersion\":0,\"persistedVersion\":0}";
        Check((await http.RegisterResultAsync()).ActiveVersion == 0, "Explicit zero revision was treated as absent.");
        handler.Body = "{\"success\":true}";
        Check(await http.RegisterAsync(), "Legacy HTTP success no longer works.");
    }

    private static async Task CheckServerAsync()
    {
        await using var fixture = await Fixture.Create();
        var service = new GatewayRegistryGrpcService(fixture.Config, NullLogger<GatewayRegistryGrpcService>.Instance);
        var context = new SecurityChecks.CallContext(new());
        Check((await service.RegisterService(new(), context)).Status == "ValidationRejected", "Registration validation omitted status.");
        Check((await service.UpdateDestinations(new(), context)).Status == "ValidationRejected", "Update validation omitted status.");
        Check((await service.UnregisterService(new() { ServiceId = "missing" }, context)).Status == "NotFound", "Missing service omitted status.");
        var request = new RegisterServiceRequest { ServiceId = "contract-service", ServiceName = "contract-cluster", InstanceId = "one" };
        request.Paths.Add("/contract/{**rest}");
        request.Destinations.Add(new Destination { DestinationId = "one", Address = "http://localhost:9001/", Enabled = true });
        var applied = await service.RegisterService(request, context);
        Check(applied.Success && applied.Status == "Applied" && applied.HasActiveVersion && applied.HasPersistedVersion, "Registration lost confirmed revisions: " + applied.Status + ": " + applied.Message);
        var update = new UpdateDestinationsRequest { ServiceId = request.ServiceId, InstanceId = "one", ExpectedVersion = applied.ActiveVersion - 1 };
        update.Destinations.Add(new Destination { DestinationId = "one", Address = "http://localhost:9002/", Enabled = true });
        Check((await service.UpdateDestinations(update, context)).Status == "VersionConflict", "Stale replacement was not classified.");
        update.ExpectedVersion = applied.ActiveVersion;
        update.Destinations.Add(update.Destinations[0].Clone());
        Check((await service.UpdateDestinations(update, context)).Status == "ValidationRejected", "Duplicate destinations reached dictionary construction.");
        update.Destinations.RemoveAt(1);
        await fixture.Sql("CREATE TRIGGER reject_contract BEFORE INSERT ON yarp_destinations BEGIN SELECT RAISE(ABORT, 'fixture-secret'); END;");
        var stored = await service.UpdateDestinations(update, context);
        Check(stored.Status == "StorageFailed" && stored.PersistedVersion == stored.ActiveVersion && !stored.Message.Contains("fixture-secret"), "Storage failure lost its transaction outcome.");
        await fixture.Sql("DROP TRIGGER reject_contract;");
        fixture.Publisher.Fail = true;
        var activation = await service.UpdateDestinations(update, context);
        Check(activation.Status == "ActivationFailed" && activation.PersistedVersion > activation.ActiveVersion, "Activation failure lost the stored revision.");
        try { await service.RegisterService(null!, context); throw new Exception("Unexpected server error was hidden."); }
        catch (RpcException failure) { Check(failure.StatusCode == StatusCode.Internal && !failure.Status.Detail.Contains("reference"), "Internal error leaked implementation details."); }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ReplyHandler : HttpMessageHandler
    {
        public int Code = 200;
        public string Body = "{\"success\":true}";
        public Exception? Failure;
        public bool Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Failure != null) throw Failure;
            return new((HttpStatusCode)Code) { Content = new StringContent(Body) };
        }
    }
    private sealed class ReplyClient : GatewayRegistry.GatewayRegistryClient
    {
        public string Status = "Applied";
        public bool Success = true, Block;
        public long? Stored, Active;
        public Exception? Failure;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AsyncUnaryCall<T> Reply<T>(T response, CallOptions options)
        {
            Check(options.Deadline != null && options.CancellationToken != default || options.Deadline > DateTime.UtcNow, "RPC deadline was omitted.");
            async Task<T> Run()
            {
                Entered.TrySetResult();
                if (Block) await Task.Delay(Timeout.Infinite, options.CancellationToken);
                if (Failure != null) throw Failure;
                return response;
            }
            return new(Run(), Task.FromResult(new Metadata()), () => Grpc.Core.Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override AsyncUnaryCall<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CallOptions options)
            => Reply(new CapabilitiesResponse { ProtocolVersion = 1 }, options);
        public override AsyncUnaryCall<RegisterServiceResponse> RegisterServiceAsync(RegisterServiceRequest request, CallOptions options)
        {
            var reply = new RegisterServiceResponse { Success = Success, Status = Status, Message = "fixture" };
            if (Stored.HasValue) reply.PersistedVersion = Stored.Value;
            if (Active.HasValue) reply.ActiveVersion = Active.Value;
            return Reply(reply, options);
        }
        public override AsyncUnaryCall<UnregisterServiceResponse> UnregisterServiceAsync(UnregisterServiceRequest request, CallOptions options)
        {
            var reply = new UnregisterServiceResponse { Success = Success, Status = Status, Message = "fixture" };
            if (Stored.HasValue) reply.PersistedVersion = Stored.Value;
            if (Active.HasValue) reply.ActiveVersion = Active.Value;
            return Reply(reply, options);
        }
        public override AsyncUnaryCall<UpdateDestinationsResponse> UpdateDestinationsAsync(UpdateDestinationsRequest request, CallOptions options)
        {
            var reply = new UpdateDestinationsResponse { Success = Success, Status = Status, Message = "fixture" };
            if (Stored.HasValue) reply.PersistedVersion = Stored.Value;
            if (Active.HasValue) reply.ActiveVersion = Active.Value;
            return Reply(reply, options);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
