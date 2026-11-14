using System.Net;
using System.Text.Json;
using Wafish.Models;
using Wafish.Storage.Entities;

internal static class NeedSixEndpointSliceChecks
{
    private static ServiceDiscoveryExecutionConfig Config => new() { Mode = "KubernetesEndpointSlice", Endpoint = "https://kubernetes.test", ServiceName = "orders", Namespace = "default", PortName = "http" };
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("EndpointSlice: paginated slices merge ready IPv4 and IPv6 destinations with exact port selection", async () =>
        {
            await using var fixture = await DiscoveryRecoveryChecks.Fixture.Create();
            var seen = new List<string>();
            fixture.Handler.Respond = (request, _) =>
            {
                seen.Add(request.RequestUri!.AbsoluteUri);
                Check(request.Headers.Authorization is null, "Discovery sent service-account credentials without opt-in.");
                Check(request.RequestUri.AbsolutePath == "/apis/discovery.k8s.io/v1/namespaces/default/endpointslices" &&
                    Uri.UnescapeDataString(request.RequestUri.Query).Contains("labelSelector=kubernetes.io/service-name=orders"), "Incorrect discovery selector.");
                return Reply(seen.Count == 1 ? List([Slice("IPv4", "10.0.0.1"), Slice("IPv4", "10.0.0.2", new { ready = false }),
                    Slice("IPv4", "10.0.0.3", new { ready = true, terminating = true }), Slice("IPv4", "10.0.0.4", new { serving = false })], "opaque+/=")
                    : List([Slice("IPv6", "2001:db8::1"), Slice("IPv4", "10.0.0.1"), Slice("IPv4", "10.0.0.5", new { ready = (bool?)null })]));
            };
            var endpoints = await fixture.Service.ResolveEndpointsAsync(Config, default);
            Check(endpoints.Order().SequenceEqual(new[] { "http://10.0.0.1:8080/", "http://10.0.0.5:8080/", "http://[2001:db8::1]:8080/" }.Order()), "Readiness, address family, deduplication, or ports changed.");
            Check(seen.Count == 2 && seen[1].Contains("continue=opaque%2B%2F%3D"), "Continuation was not encoded.");
            fixture.Handler.Respond = (_, _) => Reply(List([Slice("IPv4", "10.0.0.1")]));
            Check((await fixture.Service.ResolveEndpointsAsync(Config with { PortName = "missing" }, default)).Count == 0, "Port selection included another port.");
            Check((await fixture.Service.ResolveEndpointsAsync(Config with { PortName = null, Scheme = "https" }, default)).Count == 2, "Unfiltered TCP ports were lost.");
        });
        await run("EndpointSlice: malformed pages, repeated tokens, excess bodies, and insecure token transport fail safely", async () =>
        {
            await using var fixture = await DiscoveryRecoveryChecks.Fixture.Create();
            var valid = List([Slice("IPv4", "10.0.0.1")]);
            foreach (var body in new[] { "{}", "null", valid.Replace("IPv4", "FQDN"), valid.Replace("10.0.0.1", "host.test"),
                valid.Replace("orders", "another"), valid.Replace("default", "another"), valid.Replace("8080", "0"),
                List([Slice("IPv4", "10.0.0.1", new { ready = "yes" })]), new string(' ', 4 * 1024 * 1024 + 1) })
            {
                fixture.Handler.Respond = (_, _) => Reply(body);
                await Reject(() => fixture.Service.ResolveEndpointsAsync(Config, default));
            }
            fixture.Handler.Respond = (_, _) => Reply(List([], "repeat"));
            await Reject(() => fixture.Service.ResolveEndpointsAsync(Config, default));
            var calls = 0;
            fixture.Handler.Respond = (_, _) => Reply(List([], ++calls == 1 ? "next" : "", calls.ToString()));
            await Reject(() => fixture.Service.ResolveEndpointsAsync(Config, default));
            var before = fixture.Handler.Calls;
            await Reject(() => fixture.Service.ResolveEndpointsAsync(Config with { UseServiceAccountToken = true, Endpoint = "http://kubernetes.test" }, default));
            Check(fixture.Handler.Calls == before, "Insecure credentials reached the transport.");
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Respond = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(); };
            var pending = fixture.Service.ResolveEndpointsAsync(Config, cancel.Token); await entered.Task; cancel.Cancel();
            try { await pending; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = fixture.Service.ResolveEndpointsAsync(Config, default); await entered.Task;
            fixture.Clock.Advance(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
            try { await pending; throw new Exception("Timeout ignored."); } catch (OperationCanceledException) { }
        });
        await run("EndpointSlice: refresh removes retired endpoints and retains the last valid set on provider failure", async () =>
        {
            var binding = new PluginBindingEntity { Id = "discovery", PluginId = "service-discovery", Scope = PluginBindingScope.Cluster,
                ScopeId = "cluster", ConfigJson = JsonSerializer.Serialize(Config, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            await using var fixture = await DiscoveryRecoveryChecks.Fixture.Create(binding);
            fixture.Handler.Respond = (_, _) => Reply(List([Slice("IPv4", "10.0.0.1"), Slice("IPv4", "10.0.0.2")]));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == 2, "Initial slices were not published.");
            fixture.Handler.Respond = (_, _) => Reply("{\"error\":true}"); fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == 2, "Malformed slices removed valid destinations.");
            fixture.Handler.Respond = (_, _) => Reply(List([Slice("IPv4", "10.0.0.2")])); fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Values.Single().Address == "http://10.0.0.2:8080/", "Removed slice remained active.");
            fixture.Handler.Respond = (_, _) => Reply(List([])); fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == 0, "Valid empty slice list retained removed endpoints.");
        });
    }
    private static object Slice(string family, string address, object? conditions = null) => new
    {
        metadata = new { @namespace = "default", labels = new Dictionary<string, string> { ["kubernetes.io/service-name"] = "orders" } },
        addressType = family, endpoints = new[] { new { addresses = new[] { address }, conditions = conditions ?? new { ready = true } } },
        ports = new[] { new { name = "http", protocol = "TCP", port = 8080 }, new { name = "metrics", protocol = "TCP", port = 9090 }, new { name = "dns", protocol = "UDP", port = 53 } }
    };
    private static string List(object[] slices, string continuation = "", string version = "123")
        => JsonSerializer.Serialize(new { apiVersion = "discovery.k8s.io/v1", kind = "EndpointSliceList", metadata = new { @continue = continuation, resourceVersion = version }, items = slices });
    private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is JsonException or InvalidOperationException) { return; }
        throw new Exception("Malformed discovery response was accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
