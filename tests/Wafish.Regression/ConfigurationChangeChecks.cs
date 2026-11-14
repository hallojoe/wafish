using Microsoft.Extensions.DependencyInjection;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Sqlite;
using Yarp.ReverseProxy.Configuration;

internal static class ConfigurationChangeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Configuration changes: previews validate references and leave snapshots and storage unchanged", async () =>
        {
            await using var fixture = await Create();
            var version = fixture.Config.GetDynamicConfig()!.Version;
            var active = fixture.Publisher.Current;
            var health = fixture.App.Services.GetRequiredService<Wafish.Services.ProxyConfigHealth.IProxyConfigErrorStore>();
            health.ReplaceErrors("prevalidate", [new() { Message = "existing", Source = "prevalidate" }]);
            var request = Request(version);
            var preview = await fixture.Config.PreviewConfigurationAsync(request);
            Require(preview.Valid && preview.Normalized?.Routes.Count == 1 && preview.Changes.Count == 2, "Valid preview omitted normalized changes.");
            Require(ReferenceEquals(active, fixture.Publisher.Current) && (await Repository(fixture).LoadAsync()).Version == version, "Preview published or persisted a candidate.");
            Require(health.GetErrors().Single().Message == "existing", "Preview overwrote live health errors.");
            var invalid = await fixture.Config.PreviewConfigurationAsync(request with { Clusters = [] });
            Require(!invalid.Valid && invalid.Errors.Any(e => e.Contains("missing cluster")), "Missing cluster error is not actionable.");
            Require(!(await fixture.Config.PreviewConfigurationAsync(request with { ExpectedVersion = version - 1 })).Valid, "Stale preview accepted.");
            Require(!(await fixture.Config.PreviewConfigurationAsync(request with { Routes = [request.Routes[0], request.Routes[0]] })).Valid, "Duplicate routes accepted.");
            Require(!(await fixture.Config.PreviewConfigurationAsync(request with { RouteActivation = new Dictionary<string, bool> { ["absent"] = false } })).Valid, "Unknown activation ID accepted.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => fixture.Config.PreviewConfigurationAsync(request, cancelled.Token));
            Require(fixture.Config.GetDynamicConfig()!.Version == version && ReferenceEquals(active, fixture.Publisher.Current), "Failed preview changed state.");
        });
        await run("Configuration changes: concurrent retries return one durable result and preserve identities", async () =>
        {
            await using var fixture = await Create();
            var request = Request(fixture.Config.GetDynamicConfig()!.Version);
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Config.ApplyConfigurationAsync(request, "retry")));
            Require(results.All(r => r.Success && r == results[0]), "Retries did not return the original acknowledgement.");
            Require((await Repository(fixture).LoadAsync()).Version == request.ExpectedVersion + 1, "Retries created more revisions.");
            var state = fixture.Config.GetDynamicConfig()!;
            var next = request with { ExpectedVersion = state.Version, RouteActivation = new Dictionary<string, bool> { ["route"] = false } };
            var preview = await fixture.Config.PreviewConfigurationAsync(next);
            Require(preview.Valid && preview.Changes.Single().Operation == "Updated", "Activation diff omitted.");
            Require((await fixture.Config.ApplyConfigurationAsync(next, "retry")).Status == ConfigurationUpdateStatus.VersionConflict, "Reused key accepted a changed payload.");
            Require((await fixture.Config.ApplyConfigurationAsync(next, "second")).Success, "Second change failed.");
            var updated = fixture.Config.GetDynamicConfig()!;
            Require(updated.Routes.Single().RouteUid == state.Routes.Single().RouteUid && updated.Clusters.Single().ClusterUid == state.Clusters.Single().ClusterUid, "Stable identities changed.");
            Require(!updated.Routes.Single().Enabled && fixture.Publisher.Current.Routes.Length == 0, "Disabled route remained active.");
            var remove = new ConfigurationChangeRequest(updated.Version, [], []);
            Require((await fixture.Config.PreviewConfigurationAsync(remove)).Changes.All(c => c.Operation == "Removed"), "Removal diff omitted.");
            Require((await fixture.Config.ApplyConfigurationAsync(remove, "remove")).Success, "Removal failed.");
        });
        await run("Configuration changes: receipt survives restart without applying a stale request again", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "wafish-receipts-" + Guid.NewGuid().ToString("N"));
            ConfigurationChangeRequest request;
            RouteOperationResult original;
            try
            {
                await using (var fixture = await Create(path))
                {
                    request = Request(fixture.Config.GetDynamicConfig()!.Version);
                    original = await fixture.Config.ApplyConfigurationAsync(request, "restart");
                    Require(original.Success, "Initial write failed.");
                }
                await using (var fixture = await Create(path))
                {
                    var version = (await Repository(fixture).LoadAsync()).Version;
                    Require(await fixture.Config.ApplyConfigurationAsync(request, "restart") == original, "Restart lost original result.");
                    Require((await Repository(fixture).LoadAsync()).Version == version, "Restart replay stored another revision.");
                }
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        });
        await run("Configuration changes: failed storage rolls back receipts and allows a safe retry", async () =>
        {
            await using var fixture = await Create();
            var request = Request(fixture.Config.GetDynamicConfig()!.Version);
            var active = fixture.Publisher.Current;
            await fixture.Sql("CREATE TRIGGER reject_receipt BEFORE INSERT ON configuration_receipts BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            Require((await fixture.Config.ApplyConfigurationAsync(request, "failed")).Status == ConfigurationUpdateStatus.StorageFailed, "Write failure was hidden.");
            Require(await Repository(fixture).GetReceiptAsync("failed") is null && ReferenceEquals(active, fixture.Publisher.Current), "Failed write retained receipt or activated routes.");
            await fixture.Sql("DROP TRIGGER reject_receipt;");
            Require((await fixture.Config.ApplyConfigurationAsync(request, "failed")).Success, "Rolled back key cannot be retried.");
            Require((await fixture.Config.ApplyConfigurationAsync(request, "stale")).Status == ConfigurationUpdateStatus.VersionConflict, "Stale mutation accepted.");
            Require(await Repository(fixture).GetReceiptAsync("stale") is null, "Rejected revision reserved a key.");
        });
        await run("Configuration changes: activation failure and lost acknowledgement never create a second commit", async () =>
        {
            await using var fixture = await Create();
            var request = Request(fixture.Config.GetDynamicConfig()!.Version);
            fixture.Publisher.Fail = true;
            var failed = await fixture.Config.ApplyConfigurationAsync(request, "activation");
            Require(failed.Status == ConfigurationUpdateStatus.ActivationFailed && failed.ActiveVersion == request.ExpectedVersion && failed.PersistedVersion == request.ExpectedVersion + 1, "Activation failure lost revision split.");
            Require(await fixture.Config.ApplyConfigurationAsync(request, "activation") == failed, "Activation retry changed its acknowledgement.");
            Require((await fixture.Config.ApplyConfigurationAsync(request with { ExpectedVersion = failed.PersistedVersion!.Value }, "blocked")).Status == ConfigurationUpdateStatus.ValidationRejected, "Unreconciled revision accepted another change.");
            await using var other = await Create();
            request = Request(other.Config.GetDynamicConfig()!.Version);
            await other.Sql("CREATE TRIGGER reject_completion BEFORE UPDATE ON configuration_receipts BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var pending = await other.Config.ApplyConfigurationAsync(request, "pending");
            Require(!pending.Success && pending.ActiveVersion is null && pending.PersistedVersion == request.ExpectedVersion + 1, "Lost acknowledgement invented activation state.");
            Require(await other.Config.ApplyConfigurationAsync(request, "pending") == pending, "Pending receipt was reapplied.");
            Require(other.App.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent().Count(a => a.Action == "ApplyConfiguration") == 1,
                "Lost acknowledgement omitted or duplicated the successful activation audit.");
        });
        await run("Configuration changes: unsupported storage and invalid inputs fail before commit", async () =>
        {
            await using var fixture = await ConfigurationTransactionChecks.Fixture.Create();
            var request = Request(fixture.Config.GetDynamicConfig()!.Version);
            Require(!(await fixture.Config.ApplyConfigurationAsync(request, "unsupported")).Success, "Non-atomic provider accepted key.");
            await using var supported = await Create();
            request = Request(supported.Config.GetDynamicConfig()!.Version);
            foreach (var key in new[] { "", new string('x', 129), "bad\nkey" })
                Require(!(await supported.Config.ApplyConfigurationAsync(request, key)).Success, "Invalid key accepted.");
            Require(!(await supported.Config.ApplyConfigurationAsync(request with { Clusters = [] }, "invalid")).Success, "Invalid payload committed.");
            Require(!(await supported.Config.PreviewConfigurationAsync(request with { Routes = null! })).Valid, "Null routes accepted.");
            Require(!(await supported.Config.PreviewConfigurationAsync(request with { Routes = [new RouteConfig { RouteId = "" }] })).Valid, "Empty ID accepted.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => supported.Config.ApplyConfigurationAsync(request, "cancelled", cancelled.Token));
        });
        await run("Configuration receipts: duplicate, capacity, expiry, and version checks remain atomic", async () =>
        {
            await using var fixture = await Create();
            var repository = Repository(fixture);
            var snapshot = await repository.LoadAsync();
            var request = Request(snapshot.Version);
            Require((await fixture.Config.ApplyConfigurationAsync(request, "first")).Success, "Initial receipt failed.");
            var current = await repository.LoadAsync();
            try { await repository.SaveWithReceiptAsync(current, snapshot.Version, "first", new string('0', 64)); throw new Exception("Duplicate accepted."); }
            catch (ConfigurationReceiptExistsException) { }
            try { await repository.SaveWithReceiptAsync(current, snapshot.Version, "conflict", new string('0', 64)); throw new Exception("Stale revision accepted."); }
            catch (ConfigurationVersionConflictException) { }
            Require(await repository.GetReceiptAsync("conflict") is null, "Version failure retained its receipt.");
            await fixture.Sql("UPDATE configuration_receipts SET expires_at=0;");
            Require(await repository.GetReceiptAsync("first") is null, "Expired receipt remained visible.");
            Require((await fixture.Config.ApplyConfigurationAsync(request with { ExpectedVersion = current.Version }, "first")).Success, "Expired key cannot be reused.");
            await fixture.Sql("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<9999) INSERT INTO configuration_receipts(request_key,fingerprint,version,expires_at) SELECT 'capacity-'||x, 'hash',1,9223372036854775807 FROM n;");
            Require((await fixture.Config.ApplyConfigurationAsync(request with { ExpectedVersion = current.Version + 1 }, "full")).Status == ConfigurationUpdateStatus.StorageFailed, "Receipt bound ignored.");
            Require(await repository.GetReceiptAsync("first") is not null, "Capacity evicted an unexpired receipt.");
            try { await repository.SaveWithReceiptAsync(current, current.Version, "", "short"); throw new Exception("Invalid receipt accepted."); } catch (ArgumentException) { }
            try { await repository.CompleteReceiptAsync("first", new string('x', 16385)); throw new Exception("Oversized result accepted."); } catch (ArgumentException) { }
        });
    }
    private static Task<ConfigurationTransactionChecks.Fixture> Create(string? path = null) => ConfigurationTransactionChecks.Fixture.Create(path,
        configure: services => services.AddSingleton<IConfigurationSnapshotRepository>(sp => sp.GetRequiredService<SqliteConfigurationSnapshotRepository>()));
    private static SqliteConfigurationSnapshotRepository Repository(ConfigurationTransactionChecks.Fixture fixture) => fixture.App.Services.GetRequiredService<SqliteConfigurationSnapshotRepository>();
    private static ConfigurationChangeRequest Request(long version) => new(version,
        [new RouteConfig { RouteId = "route", ClusterId = "cluster", Match = new RouteMatch { Path = "/test/{**catch-all}" } }],
        [new ClusterConfig { ClusterId = "cluster", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:1/" } } }]);
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
