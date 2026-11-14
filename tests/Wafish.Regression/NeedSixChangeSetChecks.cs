using Microsoft.Extensions.DependencyInjection;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Sqlite;
using Yarp.ReverseProxy.Configuration;

internal static class NeedSixChangeSetChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Change sets: preview normalizes candidate bindings without storage, publication, or audit", async () =>
        {
            await using var fixture = await Create();
            var request = await Request(fixture);
            var active = fixture.Publisher.Current;
            var audit = fixture.App.Services.GetRequiredService<IConfigChangeAuditLog>();
            var count = audit.Count;
            var preview = await fixture.Config.PreviewChangeSetAsync(request, "operator");
            Check(preview.Valid && preview.Changes.Count == 3 && preview.Actor == "operator", "Preview omitted the combined diff: " + string.Join("; ", preview.Errors));
            Check(preview.Normalized!.Bindings.Single().RouteUid is not null, "Preview omitted stable binding identity.");
            Check(ReferenceEquals(active, fixture.Publisher.Current) && (await Bindings(fixture).GetBindingsAsync()).Count == 0 && audit.Count == count, "Preview changed live state.");
            foreach (var invalid in new[]
            {
                request with { Configuration = request.Configuration with { Clusters = [] } },
                request with { Bindings = [new() { Id = "bad", PluginId = "response-cache", Scope = PluginBindingScope.Route, ScopeId = "missing" }] },
                request with { Bindings = [new() { Id = "bad", PluginId = "response-cache", Scope = PluginBindingScope.Route, ScopeId = "route", ConfigJson = "{\"ttlSeconds\":-1}" }] }
            })
                Check(!(await fixture.Config.PreviewChangeSetAsync(invalid, "operator")).Valid, "Invalid combined candidate accepted.");
            Check((await Repository(fixture).LoadAsync()).Version == request.Configuration.ExpectedVersion, "Rejected preview changed storage.");
        });
        await run("Change sets: apply commits both revisions once and records one correlated actor outcome", async () =>
        {
            await using var fixture = await Create();
            var request = await Request(fixture);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Config.ApplyChangeSetAsync(request, "operator", "combined")));
            var result = results[0];
            Check(result.Configuration.Success && results.All(r => r == result), "Repeated apply returned different acknowledgements: " + result);
            Check(result.ActiveBindingRevision == result.PersistedBindingRevision && result.PersistedBindingRevision > request.ExpectedBindingRevision, "Binding revision was not activated.");
            var stored = await Bindings(fixture).GetBindingsSnapshotAsync();
            Check(stored.Bindings.Single().RouteUid == fixture.Config.GetDynamicConfig()!.Routes.Single().RouteUid, "Binding UID did not address the committed route.");
            Check(fixture.Publisher.Current.RoutePlugins["route"].Single().PluginId == "response-cache", "Combined binding was not active.");
            var audit = fixture.App.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent().Where(a => a.Action == "ApplyChangeSet").ToArray();
            Check(audit.Length == 1 && audit[0].Operator == "operator" && audit[0].Target == "combined", "Audit duplicated or lost the actor.");
            Check((await fixture.Config.ApplyChangeSetAsync(request, "different", "combined")).Configuration.Status == ConfigurationUpdateStatus.VersionConflict, "Receipt allowed a different actor.");
            var next = await Request(fixture);
            Check((await fixture.Config.ApplyChangeSetAsync(next with { Bindings = [] }, "operator", "remove")).Configuration.Success, "Binding removal failed.");
            Check((await Bindings(fixture).GetBindingsAsync()).Count == 0 && !fixture.Publisher.Current.RoutePlugins.ContainsKey("route"), "Removed binding remained active.");
        });
        await run("Change sets: binding write failure rolls back configuration and receipt; stale bindings conflict", async () =>
        {
            await using var fixture = await Create();
            var request = await Request(fixture);
            var active = fixture.Publisher.Current;
            await fixture.Sql("CREATE TRIGGER reject_combined BEFORE INSERT ON plugin_bindings BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var failure = await fixture.Config.ApplyChangeSetAsync(request, "operator", "rollback");
            Check(failure.Configuration.Status == ConfigurationUpdateStatus.StorageFailed, "Binding failure was hidden: " + failure);
            Check((await Repository(fixture).LoadAsync()).Version == request.Configuration.ExpectedVersion &&
                await Repository(fixture).GetReceiptAsync("set:rollback") is null && ReferenceEquals(active, fixture.Publisher.Current), "Combined write partially committed.");
            await fixture.Sql("DROP TRIGGER reject_combined;");
            var competing = new PluginBindingEntity { Id = "other", PluginId = "response-cache", Scope = PluginBindingScope.Route, ScopeId = "route" };
            await Bindings(fixture).UpsertBindingAsync(competing);
            Check((await fixture.Config.ApplyChangeSetAsync(request, "operator", "stale")).Configuration.Status == ConfigurationUpdateStatus.VersionConflict, "Stale binding revision accepted.");
            Check((await Repository(fixture).LoadAsync()).Version == request.Configuration.ExpectedVersion && ReferenceEquals(active, fixture.Publisher.Current), "Conflict changed configuration.");
            request = await Request(fixture);
            Check((await fixture.Config.ApplyChangeSetAsync(request, "operator", "rollback")).Configuration.Success, "Rolled back receipt prevented retry.");
        });
        await run("Change sets: failed activation preserves stored revisions and retry acknowledgement", async () =>
        {
            await using var fixture = await Create();
            var request = await Request(fixture);
            fixture.Publisher.Fail = true;
            var result = await fixture.Config.ApplyChangeSetAsync(request, "operator", "activation");
            Check(result.Configuration.Status == ConfigurationUpdateStatus.ActivationFailed && result.Configuration.ActiveVersion == request.Configuration.ExpectedVersion &&
                result.Configuration.PersistedVersion == request.Configuration.ExpectedVersion + 1 && result.ActiveBindingRevision != result.PersistedBindingRevision, "Activation failure lost separate revisions: " + result);
            Check(await fixture.Config.ApplyChangeSetAsync(request, "operator", "activation") == result, "Retry changed activation acknowledgement.");
            Check(!(await fixture.Config.ApplyChangeSetAsync(await Request(fixture), "operator", "blocked")).Configuration.Success, "Unreconciled change set accepted another write.");
        });
        await run("Change sets: optional providers, cancellation, and lost receipts preserve mutation ownership", async () =>
        {
            await using (var unsupported = await ConfigurationTransactionChecks.Fixture.Create())
            {
                var request = await Request(unsupported);
                Check(!(await unsupported.Config.PreviewChangeSetAsync(request, "operator")).Valid, "Unsupported preview accepted.");
                Check((await unsupported.Config.ApplyChangeSetAsync(request, "operator", "unsupported")).Configuration.Status == ConfigurationUpdateStatus.ValidationRejected, "Unsupported storage accepted a combined write.");
            }
            await using var fixture = await Create();
            var candidate = await Request(fixture);
            Check(!(await fixture.Config.ApplyChangeSetAsync(candidate, "operator", "")).Configuration.Success, "Empty receipt key accepted.");
            Check(!(await fixture.Config.ApplyChangeSetAsync(candidate, "", "actor")).Configuration.Success, "Empty actor accepted.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await fixture.Config.ApplyChangeSetAsync(candidate, "operator", "cancelled", cancelled.Token); throw new Exception("Cancelled apply succeeded."); } catch (OperationCanceledException) { }
            Check(await Repository(fixture).GetReceiptAsync("set:cancelled") is null, "Cancellation reserved a receipt.");
            await fixture.Sql("CREATE TRIGGER reject_combined_receipt BEFORE UPDATE ON configuration_receipts BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var pending = await fixture.Config.ApplyChangeSetAsync(candidate, "operator", "pending");
            Check(pending.Configuration.Status == ConfigurationUpdateStatus.ActivationFailed && pending.Configuration.ActiveVersion is null, "Lost receipt invented activation acknowledgement.");
            var repeated = await fixture.Config.ApplyChangeSetAsync(candidate, "operator", "pending");
            Check(repeated.Configuration.PersistedVersion == candidate.Configuration.ExpectedVersion + 1 &&
                (await Repository(fixture).LoadAsync()).Version == repeated.Configuration.PersistedVersion, "Lost receipt caused another commit.");
        });
        await run("Change sets: runtime dependencies resolve after plugins can access core configuration", async () =>
        {
            await using var fixture = await ConfigurationTransactionChecks.Fixture.Create(configure: services =>
            {
                services.AddSingleton<IConfigurationSnapshotRepository>(sp => sp.GetRequiredService<SqliteConfigurationSnapshotRepository>());
                services.AddSingleton<Wafish.Plugins.IGatewayPlugin, ConfigurationDependentPlugin>();
            });
            Check((await fixture.Config.PreviewChangeSetAsync(await Request(fixture), "operator")).Valid, "Plugin dependency caused a configuration construction cycle.");
        });

    }
    private static Task<ConfigurationTransactionChecks.Fixture> Create() => ConfigurationTransactionChecks.Fixture.Create(configure: services =>
        services.AddSingleton<IConfigurationSnapshotRepository>(sp => sp.GetRequiredService<SqliteConfigurationSnapshotRepository>()));
    private static SqliteConfigurationSnapshotRepository Repository(ConfigurationTransactionChecks.Fixture fixture) => fixture.App.Services.GetRequiredService<SqliteConfigurationSnapshotRepository>();
    private static IPluginConfigurationRepository Bindings(ConfigurationTransactionChecks.Fixture fixture) => fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
    private static async Task<ConfigurationChangeSetRequest> Request(ConfigurationTransactionChecks.Fixture fixture)
        => new(new(fixture.Config.GetDynamicConfig()!.Version,
            [new RouteConfig { RouteId = "route", ClusterId = "cluster", Match = new() { Path = "/items" } }],
            [new ClusterConfig { ClusterId = "cluster", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:12345/" } } }]),
            (await Bindings(fixture).GetBindingsSnapshotAsync()).Revision,
            [new() { Id = "cache", PluginId = "response-cache", Scope = PluginBindingScope.Route, ScopeId = "route", ConfigJson = "{\"enabled\":true,\"ttlSeconds\":20}" }]);
    private sealed class ConfigurationDependentPlugin(IDynamicYarpConfigService configuration) : Wafish.Plugins.IGatewayPlugin
    {
        public string PluginId => "configuration-dependent";
        public string DisplayName => "Configuration dependent test plugin";
        public string Version => "1.0";
        public Wafish.Plugins.PluginManifest Manifest => new(PluginId, DisplayName, Version, [], [], 100, new(), []);
        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { _ = configuration.GetDynamicConfig(); }
        public void ConfigureMiddleware(Microsoft.AspNetCore.Builder.IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(Microsoft.AspNetCore.Builder.IReverseProxyApplicationBuilder pipeline) { }
        public void ConfigureDashboard(Wafish.Plugins.IPluginDashboardBuilder builder) { }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
