using Microsoft.Extensions.DependencyInjection;
using Wafish.Storage;
using Wafish.Storage.Entities;

/// <summary>Provider-neutral checks. Supply a fresh isolated fixture for every case.</summary>
internal static class StorageConformanceChecks
{
    internal interface IFixture : IAsyncDisposable { IServiceProvider Services { get; } }
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<IFixture> create)
    {
        await run("Storage conformance: atomic rollback, null values, stable identities, and stale writers", async () =>
        {
            await using var fixture = create();
            var repository = fixture.Services.GetRequiredService<IConfigurationSnapshotRepository>();
            var cluster = new ClusterEntity { ClusterId = "cluster", ClusterUid = "cluster-uid" };
            var route = new RouteEntity { RouteId = "route", RouteUid = "route-uid", ClusterId = cluster.ClusterId, ClusterUid = cluster.ClusterUid, MatchPath = "/route" };
            var destination = new DestinationEntity { ClusterId = cluster.ClusterId, DestinationId = "destination", Address = "http://localhost:1/" };
            await repository.SaveAsync(new(1, [route], [cluster], [destination]), 0);
            var saved = await repository.LoadAsync();
            Check(saved.Routes.Single().Metadata == null && saved.Clusters.Single().HealthCheckConfig == null, "Null values changed.");
            var failed = false;
            try { await repository.SaveAsync(new(2, [], [cluster], [destination, destination]), 1); }
            catch { failed = true; }
            Check(failed && (await repository.LoadAsync()).Version == 1 && (await repository.LoadAsync()).Routes.Count == 1, "Failed transaction exposed partial writes.");
            route.RouteId = "renamed";
            await repository.SaveAsync(new(2, [route], [cluster], [destination]), 1);
            saved = await repository.LoadAsync();
            Check(saved.Routes.Single().RouteUid == "route-uid" && saved.Clusters.Single().ClusterUid == "cluster-uid", "Rename replaced stable identities.");
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                try { await repository.SaveAsync(saved with { Version = 3 }, 2); return true; }
                catch (ConfigurationVersionConflictException) { return false; }
            })));
            Check(outcomes.Count(x => x) == 1 && (await repository.LoadAsync()).Version == 3, "Concurrent stale writers both committed.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Cancelled(repository.LoadAsync(cancellation.Token));
            await Cancelled(repository.SaveAsync(saved with { Version = 4 }, 3, cancellation.Token));
            Check((await repository.LoadAsync()).Version == 3, "Cancelled save changed state.");
        });
        await run("Storage conformance: stable log pages, bounded limits, overflow, filters, and cancellation", async () =>
        {
            await using var fixture = create();
            var logs = fixture.Services.GetRequiredService<IProxyLogRepository>();
            var entries = Enumerable.Range(0, 1103).Select(i => new ProxyLogMetaEntity
            {
                Timestamp = "2026-11-14T12:00:00.0000000Z", EventType = "ProxyResponse", Level = "Information",
                RouteId = "same-route", StatusCode = 200, UpstreamPath = "/page/" + i, TraceId = "trace-'-%-" + i
            }).ToArray();
            await logs.WriteBatchAsync(entries, entries.Select(_ => (ProxyLogBodyEntity?)null).ToArray());
            var seen = new List<long>();
            for (var page = 1; page <= 30; page++)
            {
                var result = await logs.SearchAsync(page, 37, routeId: "same-route");
                Check(result.TotalCount == 1103, "Stable page count changed.");
                seen.AddRange(result.Items.Select(x => x.Id));
            }
            Check(seen.Count == 1103 && seen.Distinct().Count() == 1103 && seen.SequenceEqual(seen.OrderDescending()), "Tied timestamps caused missing or duplicate rows.");
            Check((await logs.SearchAsync(1, int.MaxValue)).Items.Count == RepositoryQueryLimits.MaximumPageSize, "Unbounded page allocation.");
            Check((await logs.SearchAsync(0, -1)).Items.Count == 1, "Invalid paging bounds were not normalized.");
            Check((await logs.SearchAsync(int.MaxValue, int.MaxValue)).Items.Count == 0, "Offset overflow returned an early page.");
            Check((await logs.SearchAsync(1, 10, keyword: "' OR 1=1 --")).TotalCount == 0, "Filter escaped its SQL parameter.");
            try { await logs.SearchAsync(1, 10, keyword: new string('x', 1025)); throw new Exception("Oversized filter accepted."); }
            catch (ArgumentException) { }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Cancelled(logs.SearchAsync(1, 10, ct: cancellation.Token));
            Check(await logs.GetMetaByIdAsync(long.MaxValue) == null && await logs.GetBodyByMetaIdAsync(long.MaxValue) == null, "Missing detail was not null.");
        });
        await run("Storage conformance: bounded histories and deterministic ordering on timestamp ties", async () =>
        {
            await using var fixture = create();
            var audit = fixture.Services.GetRequiredService<IAuditLogRepository>();
            var history = fixture.Services.GetRequiredService<IConfigHistoryRepository>();
            var conversations = fixture.Services.GetRequiredService<IAIConversationRepository>();
            var analysis = fixture.Services.GetRequiredService<IAIAnalysisRepository>();
            var deliveries = fixture.Services.GetRequiredService<IWebhookDeliveryRecordRepository>();
            var timestamp = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 503; i++)
            {
                var id = i.ToString("D4");
                await audit.SaveAuditLogAsync(new() { Id = id, Action = "test", Target = "target", Timestamp = timestamp });
                await history.SaveConfigHistoryAsync(new() { VersionId = id, ConfigData = "{}", CreatedAt = timestamp });
                await conversations.SaveMessageAsync(new() { SessionId = id, Content = "test", CreatedAt = timestamp });
                await conversations.SaveMessageAsync(new() { SessionId = "shared", Content = id, CreatedAt = timestamp });
                await analysis.SaveAnalysisAsync(new() { AnalysisType = "test", Content = id, Severity = 2, CreatedAt = timestamp });
                await deliveries.AddAsync(new() { Platform = "test", EventType = "test", CreatedAt = timestamp }, maxRecords: 1000);
            }
            var audits = await audit.GetAuditLogsAsync(int.MaxValue);
            var histories = await history.GetConfigHistoryListAsync(int.MaxValue);
            Check(audits.Count == 500 && audits.Select(x => x.Id).SequenceEqual(audits.Select(x => x.Id).OrderDescending()), "Audit order or bound changed.");
            Check(histories.Count == 500 && histories.Select(x => x.VersionId).SequenceEqual(histories.Select(x => x.VersionId).OrderDescending()), "History order or bound changed.");
            Check((await audit.GetAuditLogsByTargetAsync("target", int.MaxValue)).Count == 500, "Target audit limit was ignored.");
            var sessions = await conversations.ListSessionsAsync(int.MaxValue);
            Check(sessions.Count == 500 && sessions.Select(x => x.SessionId).SequenceEqual(sessions.Select(x => x.SessionId).OrderDescending()), "Session tie ordering changed.");
            var messages = await conversations.GetSessionMessagesAsync("shared", int.MaxValue);
            Check(messages.Count == 500 && messages.Select(x => x.Id).SequenceEqual(messages.Select(x => x.Id).Order()), "Message history is not bounded and chronological.");
            Check((await analysis.GetRecentAsync(int.MaxValue)).Count == 500 && (await analysis.GetRecentAsync(int.MaxValue, "test")).Count == 500
                && (await analysis.GetBySeverityAsync(1, int.MaxValue)).Count == 500, "Analysis limit was ignored.");
            Check((await deliveries.GetRecentAsync(int.MaxValue)).Count == 500, "Delivery limit was ignored.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Cancelled(audit.GetAuditLogsAsync(ct: cancellation.Token));
            await Cancelled(history.GetConfigHistoryListAsync(ct: cancellation.Token));
        });
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
