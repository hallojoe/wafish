using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WafIsh.Dashboard.Infrastructure.Auth;
using WafIsh.Dashboard.Infrastructure.Performance;
using WafIsh.Dashboard.Infrastructure.Plugin;
using WafIsh.Dashboard.Modules.Dashboard.Controllers;
using WafIsh.Services.ProxyConfigHealth;

internal static class SonarRegressionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Audit: initial history loading cannot duplicate concurrent writes", async () =>
        {
            var repo = new DelayedAuditRepository();
            var audit = new WafIsh.Dashboard.Modules.GatewayConfig.Services.ConfigChangeAuditLog(repo,
                NullLogger<WafIsh.Dashboard.Modules.GatewayConfig.Services.ConfigChangeAuditLog>.Instance);
            audit.RecordSuccess("UpdateRoute", "route");
            await repo.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(repo.Saved.Task, Task.Delay(100));
            Check(audit.Count == 1 && !repo.Saved.Task.IsCompleted, "New audit persisted before history finished loading.");
            repo.ReleaseRead.SetResult();
            await repo.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(audit.GetRecent().Count == 1 && audit.GetRecent()[0].Target == "route", "Initial history duplicated a live audit entry.");
        });

        await run("Logging: batch disposal clears data and returns pooled storage only once", () =>
        {
            using var pool = new LogEntryPool();
            var batch = new LogBatch(pool);
            Check(batch.TryAdd(new LogEntryStruct(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1, 200, 10, "route", "cluster", "GET", "trace")), "An empty batch rejected an entry.");
            var entries = batch.Entries;
            batch.Dispose();
            batch.Dispose();
            Check(batch.Count == 0 && batch.Capacity == 0 && !batch.TryAdd(default), "Disposed batch retained usable storage.");
            Check(entries.All(entry => entry == default), "Disposed log data was retained.");
            return Task.CompletedTask;
        });

        await run("Logging: compressed bodies decode safely and request bodies remain readable", async () =>
        {
            const string message = "hello æøå";
            foreach (var encoding in new[] { "gzip", "deflate", "br" })
            {
                await using var compressed = new MemoryStream();
                await using (Stream compressor = encoding switch
                {
                    "gzip" => new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Compress, true),
                    "deflate" => new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionMode.Compress, true),
                    _ => new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionMode.Compress, true)
                })
                {
                    await compressor.WriteAsync(Encoding.UTF8.GetBytes(message));
                }

                var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                context.Request.Body = compressed;
                context.Request.ContentLength = compressed.Length;
                context.Request.ContentType = "text/plain; charset=utf-8";
                context.Request.Headers.ContentEncoding = encoding;
                Check(await WafIsh.Plugin.ProxyLog.Services.ProxyLogBodyReader.ReadRequestBodyAsync(context.Request, 1024) == message, "Request decompression changed content.");
                Check(compressed.Position == 0 && compressed.CanRead, "Logging consumed the request body.");
                Check(await WafIsh.Plugin.ProxyLog.Services.ProxyLogBodyReader.ReadStreamAsync(compressed, 1024, "text/plain", encoding) == message, "Response decompression changed content.");
            }
        });

        await run("Migration: circuit-breaker defaults preserve explicit settings and reject malformed JSON", () =>
        {
            var migrator = new CircuitBreakerConfigMigrator();
            Check(migrator.TryMigrate("{\"failureThreshold\":7}", out var json, out _), "Valid configuration did not migrate.");
            using var result = JsonDocument.Parse(json);
            Check(result.RootElement.GetProperty("circuitOpenStatusCodes").EnumerateArray().Select(e => e.GetInt32()).SequenceEqual([502, 503, 504]), "Default status codes changed.");
            Check(result.RootElement.GetProperty("failureThreshold").GetInt32() == 7, "Existing settings were lost.");
            Check(migrator.TryMigrate("{\"circuitOpenStatusCodes\":[429],\"onOpenWebhookUrl\":\"https://example.test/hook\"}", out json, out _), "Explicit settings did not migrate.");
            using var preserved = JsonDocument.Parse(json);
            Check(preserved.RootElement.GetProperty("circuitOpenStatusCodes")[0].GetInt32() == 429, "Explicit codes were overwritten.");
            Check(!migrator.TryMigrate("{", out _, out var error) && error.Length > 0, "Malformed JSON was accepted.");
            return Task.CompletedTask;
        });

        await run("Security: plugin schema regex rejects excessive backtracking within a time bound", () =>
        {
            var validator = new PluginConfigurationSchemaValidator();
            var timer = Stopwatch.StartNew();
            var valid = validator.TryValidate(JsonSerializer.Serialize(new string('a', 10000) + "!"),
                """{"type":"string","pattern":"^(a+)+$"}""", out _, out var error);
            Check(!valid && error.Contains("time limit", StringComparison.Ordinal), "Expensive pattern did not fail closed.");
            Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Regex validation exceeded its time budget.");
            return Task.CompletedTask;
        });

        await run("Validation: plugin schemas enforce types, composition, bounds, and nested fields", () =>
        {
            var validator = new PluginConfigurationSchemaValidator();
            (string Value, string Schema, bool Valid)[] cases =
            [
                ("{}", "{}", true), ("{", "{}", false), ("{}", "[]", false),
                ("null", "{\"type\":[\"string\",\"null\"]}", true),
                ("true", "{\"type\":\"boolean\"}", true), ("1", "{\"type\":\"boolean\"}", false),
                ("1.5", "{\"type\":\"integer\"}", false), ("1", "{\"type\":\"integer\"}", true),
                ("1.5", "{\"type\":\"number\"}", true), ("null", "{\"type\":\"null\"}", true),
                ("{}", "{\"type\":\"unknown\"}", false),
                ("{}", "{\"required\":[\"name\"]}", false),
                ("{\"name\":3}", "{\"properties\":{\"name\":{\"type\":\"string\"}}}", false),
                ("{\"name\":\"ok\"}", "{\"properties\":{\"name\":{\"type\":\"string\"}},\"additionalProperties\":false}", true),
                ("{\"extra\":1}", "{\"additionalProperties\":false}", false),
                ("{}", "{\"minProperties\":1}", false), ("{\"a\":1}", "{\"maxProperties\":0}", false),
                ("[]", "{\"minItems\":1}", false), ("[1,2]", "{\"maxItems\":1}", false),
                ("[1,1.0]", "{\"uniqueItems\":true}", false),
                ("[{\"a\":1},{\"a\":1}]", "{\"uniqueItems\":true}", false),
                ("[[1],[1]]", "{\"uniqueItems\":true}", false),
                ("[1,2]", "{\"items\":{\"type\":\"integer\"}}", true),
                ("[1,\"x\"]", "{\"items\":{\"type\":\"integer\"}}", false),
                ("\"a\"", "{\"minLength\":2}", false), ("\"abc\"", "{\"maxLength\":2}", false),
                ("\"abc\"", "{\"pattern\":\"^[a-z]+$\"}", true), ("\"123\"", "{\"pattern\":\"^[a-z]+$\"}", false),
                ("\"x\"", "{\"pattern\":\"[\"}", false),
                ("\"00:00:01\"", "{\"format\":\"duration\"}", true), ("\"invalid\"", "{\"format\":\"duration\"}", false),
                ("-1", "{\"minimum\":0}", false), ("2", "{\"maximum\":1}", false),
                ("1e100", "{\"type\":\"number\"}", false),
                ("1", "{\"allOf\":[{\"minimum\":0},{\"maximum\":2}]}", true),
                ("3", "{\"allOf\":[{\"minimum\":0},{\"maximum\":2}]}", false),
                ("\"x\"", "{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"string\"}]}", true),
                ("true", "{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"string\"}]}", false),
                ("1", "{\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"number\"}]}", false),
                ("\"x\"", "{\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"string\"}]}", true),
                ("1", "{\"enum\":[1,2]}", true), ("3", "{\"enum\":[1,2]}", false),
                ("{\"x\":1,\"y\":true}", "{\"const\":{\"y\":true,\"x\":1.0}}", true),
                ("[1,2]", "{\"const\":[1,2]}", true), ("[2,1]", "{\"const\":[1,2]}", false)
            ];
            foreach (var item in cases)
            {
                var valid = validator.TryValidate(item.Value, item.Schema, out var normalized, out var error);
                Check(valid == item.Valid, $"Schema result mismatch: {item.Value}, {item.Schema}: {error}");
                Check(valid ? !string.IsNullOrEmpty(normalized) : !string.IsNullOrEmpty(error), "Validation returned no result details.");
            }
            return Task.CompletedTask;
        });

        await run("Security: TOTP matches RFC 6238 vectors and preserves authenticator enrollment", () =>
        {
            const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
            foreach (var (time, code) in new (long, string)[] { (59, "287082"), (1111111109, "081804"), (1111111111, "050471"), (1234567890, "005924"), (2000000000, "279037"), (20000000000, "353130") })
            {
                Check(TotpHelper.GenerateCode(secret, DateTimeOffset.FromUnixTimeSeconds(time)) == code, "TOTP does not match RFC 6238.");
            }

            var generated = TotpHelper.GenerateSecret();
            Check(generated.Length == 32 && generated != TotpHelper.GenerateSecret(), "Generated secrets are not independent.");
            Check(TotpHelper.ValidateCode(generated, TotpHelper.GenerateCode(generated)), "Current code was rejected.");
            Check(!TotpHelper.ValidateCode(generated, "bad") && !TotpHelper.ValidateCode(generated, "1234567"), "Malformed code was accepted.");
            var uri = TotpHelper.BuildOtpAuthUri("Waf Ish", "a+b@example.test", generated);
            Check(uri.Contains("algorithm=SHA1", StringComparison.Ordinal) && uri.Contains("Waf%20Ish", StringComparison.Ordinal), "Enrollment protocol changed.");
            return Task.CompletedTask;
        });

        await run("Validation: mutation requests require an explicit enabled value", () =>
        {
            try { JsonSerializer.Deserialize<TogglePluginRequest>("{}"); throw new InvalidOperationException("Missing Enabled was accepted."); }
            catch (JsonException) { }
            Check(JsonSerializer.Deserialize<TogglePluginRequest>("{\"Enabled\":false}") is { Enabled: false }, "Explicit false was rejected.");
            return Task.CompletedTask;
        });

        await run("Configuration: YARP failures retain route attribution and clear after successful apply", () =>
        {
            var store = new ProxyConfigErrorStore(NullLogger<ProxyConfigErrorStore>.Instance);
            var listener = new YarpConfigErrorListener(store, NullLogger<YarpConfigErrorListener>.Instance);
            var error = new InvalidOperationException("Route 'route-a' references cluster 'cluster-b'");
            listener.ConfigurationApplyingFailed([], new AggregateException(error, error));
            var captured = store.GetErrors().Single();
            Check(captured.RouteId == "route-a" && captured.ClusterId == "cluster-b", "Identifiers were not captured.");
            Check(store.GetHealth().Status == "error", "Failed apply was marked healthy.");
            listener.ConfigurationLoaded([]);
            Check(store.GetHealth().Status == "error", "Loading cleared an apply error.");
            listener.ConfigurationApplied([]);
            Check(store.GetHealth().Status == "healthy" && store.GetErrors().Count == 0, "Successful apply retained errors.");
            return Task.CompletedTask;
        });

        await run("Performance helpers: numeric limits, UTF-8, and ring-buffer ownership remain correct", () =>
        {
            CheckPerformanceHelpers();
            return Task.CompletedTask;
        });
    }

    private static unsafe void CheckPerformanceHelpers()
    {
        foreach (var a in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            foreach (var b in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            {
                Check(UnsafeOptimizations.FastMin(a, b) == Math.Min(a, b) && UnsafeOptimizations.FastMax(a, b) == Math.Max(a, b), "Min/max failed at an integer boundary.");
            }
        }

        foreach (var value in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            var bytes = Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fixed (byte* pointer = bytes)
            {
                Check(UnsafeOptimizations.TryParseInt32(pointer, bytes.Length, out var parsed) && parsed == value, "ASCII integer parsing failed.");
            }
        }
        foreach (var text in new[] { "", "+", "-", "2147483648", "-2147483649", "1x" })
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            fixed (byte* pointer = bytes)
            {
                Check(!UnsafeOptimizations.TryParseInt32(pointer, bytes.Length, out _), "Invalid integer was accepted.");
            }
        }
        Span<byte> destination = stackalloc byte[2];
        Check(UnsafeOptimizations.CopyUtf8String("ø", destination).SequenceEqual("ø"u8), "Exact-size UTF-8 destination was rejected.");
        for (var length = 0; length < 32; length++)
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
            Check(UnsafeOptimizations.Utf8Equals(bytes, bytes.ToArray()), "Equal buffers differ.");
            if (length > 0)
            {
                var changed = bytes.ToArray(); changed[^1]++;
                Check(!UnsafeOptimizations.Utf8Equals(bytes, changed), "Different buffers compare equal.");
            }
            Check(UnsafeOptimizations.FastHash(bytes) == UnsafeOptimizations.FastHash(bytes.ToArray()), "Hash is not deterministic.");
        }
        byte* prefetch = stackalloc byte[16];
        prefetch[0] = 42;
        UnsafeOptimizations.Prefetch(prefetch);
        Check(prefetch[0] == 42, "Prefetch changed buffer contents.");
        var ring = new CacheOptimizedRingBuffer<int>(3);
        Check(ring.Capacity == 4 && !ring.TryDequeue(out _), "Empty ring state is wrong.");
        for (var i = 0; i < 4; i++)
        {
            Check(ring.TryEnqueue(i), "Ring rejected available capacity.");
        }

        Check(!ring.TryEnqueue(9) && ring.Peek() == 0, "Full ring overflowed or reordered data.");
        for (var i = 0; i < 4; i++)
        {
            Check(ring.TryDequeue(out var item) && item == i, "Ring FIFO order failed.");
        }

        Check(ring.TryEnqueue(10) && ring.Peek() == 10, "Ring wraparound failed.");
        ring.Clear(); Check(ring.Count == 0, "Ring clear failed.");
        var copy = ring;
        ring.Dispose(); copy.Dispose();
        try { copy.TryEnqueue(1); throw new InvalidOperationException("Disposed buffer remained writable."); }
        catch (ObjectDisposedException) { }
        try { _ = new CacheOptimizedRingBuffer<int>(0); throw new InvalidOperationException("Invalid capacity was accepted."); }
        catch (ArgumentOutOfRangeException) { }
    }

    private sealed class DelayedAuditRepository : WafIsh.Storage.IAuditLogRepository
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WafIsh.Storage.AuditLogEntity? _entry;
        public async Task<IReadOnlyList<WafIsh.Storage.AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default)
        {
            ReadStarted.SetResult();
            await ReleaseRead.Task.WaitAsync(ct);
            return _entry is { } entry ? [entry] : [];
        }
        public Task SaveAuditLogAsync(WafIsh.Storage.AuditLogEntity audit, CancellationToken ct = default)
        {
            _entry = audit;
            Saved.SetResult();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WafIsh.Storage.AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WafIsh.Storage.AuditLogEntity>>([]);
        public Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
