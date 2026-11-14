using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Storage;
using Wafish.Storage.Sqlite;

internal static class AllocationPressureChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Allocation pressure: small cache entries compact within the shared capture budget", CacheAsync);
        await run("Allocation pressure: concurrent compression streams large JSON and excluded binary without body retention", CompressionAsync);
        await run("Allocation pressure: one-route snapshot updates avoid unrelated writes and WAL growth", SnapshotAsync);
        await run("Allocation pressure: differential writes preserve every stored field, renames, cascades, and rollback", SnapshotFieldsAsync);
    }

    private static async Task CacheAsync()
    {
        var fixture = new ResponseStreamingChecks.Fixture(capacity: 1024 * 1024, budget: 8 * 1024 * 1024)
        { Respond = context => context.Response.Body.WriteAsync(new byte[512]).AsTask() };
        await fixture.Send();
        var retained = fixture.Store.AllocatedBytes;
        Console.WriteLine($"Cache measurement: one 512-byte response retains {retained} charged bytes.");
        Check(retained < 80 * 1024, "Small cached response retained its one-MiB capture buffer.");

        var readers = Enumerable.Range(0, 8).Select(_ => new BlockingSink()).ToArray();
        var reads = readers.Select(sink => fixture.Send(context => context.Response.Body = sink)).ToArray();
        await Task.WhenAll(readers.Select(sink => sink.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Publish(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        fixture.Respond = async context =>
        {
            if (Interlocked.Increment(ref count) == 32) entered.SetResult();
            await release.Task;
            await context.Response.Body.WriteAsync(new byte[512]);
        };
        var misses = Enumerable.Range(0, 32).Select(index => fixture.Send(context => context.Request.QueryString = new("?miss=" + index))).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Store.AllocatedBytes >= retained && fixture.Store.AllocatedBytes <= fixture.Store.SizeLimitBytes,
            "Eviction released active readers or concurrent captures exceeded the budget.");
        release.SetResult(); await Task.WhenAll(misses);
        Check(fixture.Store.AllocatedBytes <= fixture.Store.SizeLimitBytes, "Compaction exceeded the capture budget.");
        fixture.Publish(3);
        await fixture.Send(context => { context.Request.Method = "HEAD"; context.Response.Body = Stream.Null; });
        Check(fixture.Store.AllocatedBytes == retained, "Evicted readers lost their retained charge.");
        foreach (var reader in readers) reader.Release.SetResult();
        await Task.WhenAll(reads);
        Check(fixture.Store.AllocatedBytes == 0, "Final reader did not release its evicted entry.");
        var saturated = new ResponseStreamingChecks.Fixture(capacity: 1024, budget: 1024 + 65536 + 1024)
        { Respond = context => context.Response.Body.WriteAsync(new byte[16]).AsTask() };
        await saturated.Send(); await saturated.Send();
        Check(saturated.Calls == 1 && saturated.Store.AllocatedBytes == saturated.Store.SizeLimitBytes,
            "Compaction borrowed budget when two buffers could not fit.");
        var empty = new ResponseStreamingChecks.Fixture { Respond = _ => Task.CompletedTask };
        await empty.Send(); await empty.Send();
        Check(empty.Calls == 1 && empty.Store.AllocatedBytes < 70 * 1024, "Empty responses were not compacted safely.");
    }

    private static async Task CompressionAsync()
    {
        var block = new byte[8192]; Array.Fill(block, (byte)'x');
        foreach (var binary in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var fixture = new ResponseStreamingChecks.Fixture(compress: true)
            {
                Respond = async context =>
                {
                    context.Response.ContentType = binary ? "application/octet-stream" : "application/json";
                    await context.Response.Body.WriteAsync(block);
                    await context.Response.Body.FlushAsync();
                    if (Interlocked.Increment(ref count) == 16) entered.SetResult();
                    await release.Task;
                    for (var index = 1; index < 2048; index++) await context.Response.Body.WriteAsync(block);
                }
            };
            var sinks = Enumerable.Range(0, 16).Select(_ => new CountingSink()).ToArray();
            var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
            var requests = sinks.Select(sink => fixture.Send(context =>
            {
                context.Request.Headers.CacheControl = "no-store";
                context.Request.Headers.AcceptEncoding = "gzip";
                context.Response.Body = sink;
            })).ToArray();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(sinks.All(sink => sink.Bytes > 0), "Compression delayed first bytes until producer completion.");
            release.SetResult(); await Task.WhenAll(requests);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            Console.WriteLine($"Compression measurement: binary={binary}, 16 x 16 MiB, allocated={allocated}, elapsedMs={timer.Elapsed.TotalMilliseconds:F1}.");
            Check(allocated < 16 * 1024 * 1024, "Streaming retained allocations proportional to complete response bodies.");
            if (binary) Check(sinks.All(sink => sink.Bytes == 16 * 1024 * 1024), "Excluded binary bytes changed.");
        }
    }

    private static async Task SnapshotAsync()
    {
        await using var fixture = new StorageReliabilityFixture();
        var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
        var repository = fixture.Provider.GetRequiredService<IConfigurationSnapshotRepository>();
        var original = await repository.LoadAsync();
        var cluster = new ClusterEntity { ClusterId = "scale" };
        var routes = Enumerable.Range(0, 3000).Select(index => new RouteEntity
        { RouteId = "route-" + index, ClusterId = cluster.ClusterId, ClusterUid = cluster.ClusterUid, MatchPath = "/route/" + index }).ToArray();
        await repository.SaveAsync(new(original.Version + 1, routes, [cluster], [new() { ClusterId = "scale", DestinationId = "one", Address = "http://localhost:5100" }]), original.Version);
        await using var inspect = await factory.CreateConnectionAsync(); await inspect.OpenAsync();
        await Execute(inspect, "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE); CREATE TABLE write_probe(kind TEXT);");
        foreach (var table in new[] { "yarp_routes", "yarp_clusters", "yarp_destinations" })
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            await Execute(inspect, $"CREATE TRIGGER probe_{table}_{operation} AFTER {operation} ON {table} BEGIN INSERT INTO write_probe VALUES ('{table}:{operation}'); END;");
        var snapshot = await repository.LoadAsync(); snapshot.Routes[1500].MatchPath = "/changed";
        await Execute(inspect, "PRAGMA wal_checkpoint(TRUNCATE);");
        var timer = Stopwatch.StartNew();
        await repository.SaveAsync(snapshot with { Version = snapshot.Version + 1 }, snapshot.Version);
        var elapsed = timer.Elapsed.TotalMilliseconds;
        using var query = inspect.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM write_probe";
        var writes = Convert.ToInt64(await query.ExecuteScalarAsync());
        var wal = new FileInfo(fixture.Path + "-wal").Length;
        Console.WriteLine($"Snapshot measurement: 3000 routes, one change, rowWrites={writes}, walBytes={wal}, elapsedMs={elapsed:F1}.");
        Check(writes <= 2 && wal < 128 * 1024, "One-route update rewrote unrelated configuration.");
        var saved = await repository.LoadAsync();
        Check(saved.Routes.Single(route => route.RouteId == "route-1500").RouteUid == routes[1500].RouteUid && saved.Destinations.Count == 1,
            "Differential write lost stable identity or an unchanged destination.");
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }

    private static async Task SnapshotFieldsAsync()
    {
        await using var fixture = new StorageReliabilityFixture();
        var repository = fixture.Provider.GetRequiredService<IConfigurationSnapshotRepository>();
        var initial = await repository.LoadAsync();
        var cluster = new ClusterEntity { ClusterId = "cluster" };
        await repository.SaveAsync(new(initial.Version + 1,
            [new() { RouteId = "route", ClusterId = "cluster", MatchPath = "/route" }], [cluster],
            [new() { ClusterId = "cluster", DestinationId = "destination", Address = "http://localhost:1/" }]), initial.Version);
        // Each field changes alone, so an omitted comparison cannot hide behind another changed field.
        foreach (var type in new[] { typeof(RouteEntity), typeof(ClusterEntity), typeof(DestinationEntity) })
        foreach (var property in type.GetProperties().Where(property => property.Name is not ("DisplayName" or "RouteId" or "ClusterId" or "DestinationId")))
        {
            var before = await repository.LoadAsync();
            object entity = type == typeof(RouteEntity) ? before.Routes[0] : type == typeof(ClusterEntity) ? before.Clusters[0] : before.Destinations[0];
            object value = property.PropertyType == typeof(bool) ? !(bool)property.GetValue(entity)! :
                property.PropertyType == typeof(int) ? 37 :
                property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?) ? new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) : "changed-" + property.Name;
            property.SetValue(entity, value);
            await repository.SaveAsync(before with { Version = before.Version + 1 }, before.Version);
            var after = await repository.LoadAsync();
            object stored = type == typeof(RouteEntity) ? after.Routes[0] : type == typeof(ClusterEntity) ? after.Clusters[0] : after.Destinations[0];
            Check(Equals(property.GetValue(stored), value), "Differential write omitted " + type.Name + "." + property.Name);
            Check(after.Destinations.Count == 1, "Changed cluster lost its destination through cascade deletion.");
        }
        var snapshot = await repository.LoadAsync();
        var uid = snapshot.Routes[0].RouteUid;
        snapshot.Routes[0].RouteId = "renamed";
        await repository.SaveAsync(snapshot with { Version = snapshot.Version + 1 }, snapshot.Version);
        var saved = await repository.LoadAsync();
        Check(saved.Routes[0].RouteUid == uid && saved.Routes[0].RouteId == "renamed", "Differential rename lost identity.");
        var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
        await using var inspect = await factory.CreateConnectionAsync(); await inspect.OpenAsync();
        await Execute(inspect, "CREATE TRIGGER reject_version BEFORE UPDATE ON configuration_version BEGIN SELECT RAISE(ABORT, 'rollback probe'); END;");
        saved.Routes[0].MatchPath = "/must-rollback";
        try { await repository.SaveAsync(saved with { Version = saved.Version + 1 }, saved.Version); throw new Exception("Injected transaction failure was ignored."); }
        catch (SqliteException) { }
        var rolledBack = await repository.LoadAsync();
        Check(rolledBack.Version == saved.Version && rolledBack.Routes[0].MatchPath != "/must-rollback", "Differential changes escaped rollback.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private class CountingSink : Stream
    {
        public long Bytes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Bytes += buffer.Length; return ValueTask.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) => Bytes += count;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Bytes; public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class BlockingSink : CountingSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); await base.WriteAsync(buffer, cancellationToken); }
    }
}
