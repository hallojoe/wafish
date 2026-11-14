using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Database = StorageRepositoryChecks.Database;

internal static class LogQuotaChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Log quotas: sustained writes retain recent metadata and account body discards", async () =>
        {
            await using var db = new Database();
            var repository = db.Get<IProxyLogRepository>();
            var writer = new SqliteProxyLogWriter(repository, NullLogger<SqliteProxyLogWriter>.Instance,
                Options.Create(new ProxyLogPluginOptions { LogMetadataQuotaBytes = 2000, LogBodyQuotaBytes = 400 }));
            for (var i = 0; i < 50; i++)
            {
                await writer.WriteBatchAsync([new LogEntry { Timestamp = DateTime.UtcNow, EventType = LogEventType.ProxyResponse,
                    Level = "Information", RouteId = "r", TraceId = i.ToString(), ResponseBody = new string('é', 100) }]);
                Require(writer.QuotaUsage.MetadataBytes <= 2000 && writer.QuotaUsage.BodyBytes <= 400, "Committed quota exceeded.");
            }
            var page = await repository.SearchAsync(1, 100);
            Require(page.Items.Count > 1 && page.Items[0].TraceId == "49", "Recent metadata was lost.");
            Require(writer.QuotaDiscardedBodies == 49 && writer.QuotaDiscardedMetadata == 50 - page.TotalCount, "Discard accounting mismatch.");
            Require(await repository.GetBodyByMetaIdAsync(page.Items[0].Id) is not null, "Recent body lost.");
            Require(page.Items.Skip(1).All(m => m.HasResponseBody == 0), "Deleted body flags remain set.");
            var settings = new ProxyLogRuntimeSettings(Options.Create(new ProxyLogPluginOptions()));
            var service = new AsyncLogPersistenceService(new ProxyLogStore(), writer, settings, null!, NullLogger<AsyncLogPersistenceService>.Instance);
            var health = await service.CheckHealthAsync();
            Require(health.Statistics!["quotaDiscardedBodies"] == 49, "Health omitted quota pressure.");
        });
        await run("Log quotas: cancellation rolls back, disabled quotas preserve data, and concurrent writes stay bounded", async () =>
        {
            await using var db = new Database();
            var repository = db.Get<IProxyLogRepository>();
            var quotaRepository = (IQuotaProxyLogRepository)repository;
            var metadata = new ProxyLogMetaEntity { Timestamp = DateTime.UtcNow.ToString("O"), EventType = "ProxyResponse", Level = "Information" };
            await quotaRepository.WriteBatchWithQuotaAsync([metadata], [new() { ResponseBody = new string('x', 2000) }], new(0, 0));
            Require((await repository.SearchAsync(1, 10)).TotalCount == 1, "Disabled quota dropped data.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await quotaRepository.WriteBatchWithQuotaAsync([metadata], [null], new(1, 1), canceled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Require((await repository.SearchAsync(1, 10)).TotalCount == 1, "Canceled write changed data.");
            await Parallel.ForEachAsync(Enumerable.Range(0, 10), async (_, ct) =>
                await quotaRepository.WriteBatchWithQuotaAsync([metadata], [new() { ResponseBody = new string('x', 100) }], new(1000, 300), ct));
            var result = await quotaRepository.WriteBatchWithQuotaAsync([], [], new(1000, 300));
            Require(result.MetadataBytes <= 1000 && result.BodyBytes <= 300, "Concurrent writers exceeded quotas.");
            try { await quotaRepository.WriteBatchWithQuotaAsync([], [], new(-1, 0)); throw new Exception("Negative quota accepted."); }
            catch (ArgumentOutOfRangeException) { }
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
