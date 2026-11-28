using System.IO.Compression;
using Wafish.Models;
using Wafish.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.Compression;

/// <summary>Route-scoped compression with a bounded prefix and immediate streaming after selection.</summary>
public sealed class CompressionMiddleware(RequestDelegate next, GatewayPluginExecutionPlanProvider plans)
{
    private readonly CompressionMetricStore? _metrics;
    private readonly TimeProvider _time = TimeProvider.System;
    [ActivatorUtilitiesConstructor]
    public CompressionMiddleware(RequestDelegate next, GatewayPluginExecutionPlanProvider plans,
        CompressionMetricStore? metrics = null, TimeProvider? timeProvider = null) : this(next, plans)
    { _metrics = metrics; _time = timeProvider ?? TimeProvider.System; }

    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) || !plans.Current.CompressionByRoute.TryGetValue(routeId, out var config) || !config.Enabled)
        { await next(context); return; }
        AppendVary(context.Response.Headers);
        context.Response.OnStarting(() => { AppendVary(context.Response.Headers); return Task.CompletedTask; });
        var encoding = SelectEncoding(context.Request.Headers.AcceptEncoding.ToArray());
        if (HttpMethods.IsHead(context.Request.Method) || context.Request.Headers.ContainsKey("Range") || HasNoTransform(context.Request.Headers))
            encoding = null;
        if (encoding == null && _metrics == null) { await next(context); return; }
        var original = context.Response.Body;
        var stream = new CompressionWriteStream(context, original, config, encoding, _time);
        var outcome = CompressionMetricOutcome.Completed;
        context.Response.Body = stream;
        try { await next(context); await stream.CompleteAsync(context.RequestAborted); }
        catch (OperationCanceledException) { outcome = CompressionMetricOutcome.Cancelled; throw; }
        catch { outcome = CompressionMetricOutcome.Faulted; throw; }
        finally
        {
            context.Response.Body = original;
            try { await stream.DisposeAsync(); }
            catch (OperationCanceledException) { outcome = CompressionMetricOutcome.Cancelled; throw; }
            catch { outcome = CompressionMetricOutcome.Faulted; throw; }
            finally { _metrics?.Record(routeId, stream.SelectedEncoding, outcome, stream.InputBytes, stream.OutputBytes, stream.CodecDuration); }
        }
    }

    private static string? SelectEncoding(string?[] values)
    {
        if (!HasValidQualities(values) || !StringWithQualityHeaderValue.TryParseStrictList(values.Select(v => v ?? "").ToArray(), out var parsed))
        {
            return null;
        }

        double Quality(string encoding)
        {
            var exact = parsed.Where(p => p.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0)
            {
                return exact.Min(p => p.Quality ?? 1);
            }

            return parsed.Where(p => p.Value == "*").Select(p => p.Quality ?? 1).DefaultIfEmpty(0).Min();
        }
        var br = Quality("br"); var gzip = Quality("gzip");
        return br > 0 && br >= gzip ? "br" : gzip > 0 ? "gzip" : null;
    }

    private static bool HasValidQualities(string?[] values)
    {
        // RFC 9110 qvalues allow 0..1 and at most three decimal places.
        // The framework numeric parser accepts additional precision, so check the original tokens too.
        foreach (var value in values)
        foreach (var item in (value ?? "").Split(','))
        {
            var parts = item.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length == 1) continue;
            if (parts.Length != 2 || !parts[1].StartsWith("q=", StringComparison.OrdinalIgnoreCase)) return false;
            var quality = parts[1].AsSpan(2);
            if (quality.Length == 0 || quality[0] is not ('0' or '1')) return false;
            if (quality.Length == 1) continue;
            if (quality.Length > 5 || quality[1] != '.') return false;
            foreach (var digit in quality[2..])
                if (digit is < '0' or > '9' || (quality[0] == '1' && digit != '0')) return false;
        }
        return true;
    }

    internal static bool CanCompress(HttpContext context, CompressionExecutionConfig config) =>
        !context.Response.HasStarted && context.Response.StatusCode is >= 200 and < 300 and not 204 and not 205 &&
        !context.Response.Headers.ContainsKey("Content-Encoding") && !context.Response.Headers.ContainsKey("Content-Range") &&
        !HasNoTransform(context.Response.Headers) &&
        config.MimeTypes.Any(t => t.Equals(context.Response.ContentType?.Split(';')[0].Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (context.Response.ContentLength == null || context.Response.ContentLength >= config.MinResponseSize);

    private static bool HasNoTransform(IHeaderDictionary headers) =>
        headers.TryGetValue("Cache-Control", out var value) &&
        (!CacheControlHeaderValue.TryParse(value.ToString(), out var control) || control.NoTransform);

    private static void AppendVary(IHeaderDictionary headers)
    {
        var vary = headers.Vary.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!vary.Any(v => v == "*" || v.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Append("Vary", "Accept-Encoding");
        }
    }

    private sealed class CompressionWriteStream(HttpContext context, Stream destination, CompressionExecutionConfig config, string? encoding, TimeProvider time) : Stream
    {
        // Unknown-length responses never require more than 64 KiB of prefix storage.
        private byte[]? _prefix;
        private int _count;
        private Stream? _output;
        private bool _completed;
        private bool _compressed;
        private long _codecTicks;
        private readonly MeasuredDestination _measured = new(destination, time);
        public long InputBytes { get; private set; }
        public long OutputBytes => _measured.Bytes;
        public string SelectedEncoding => _compressed ? encoding! : "identity";
        public TimeSpan CodecDuration => TimeSpan.FromTicks(_codecTicks);

        private async ValueTask SelectAsync(bool compress, CancellationToken token)
        {
            if (compress)
            {
                context.Response.Headers.ContentEncoding = encoding;
                context.Response.ContentLength = null;
                // A strong entity tag identifies the original representation only.
                var etag = context.Response.Headers.ETag.ToString();
                if (etag.Length > 0 && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.ETag = "W/" + etag;
                }

                var level = config.CompressionLevel switch { "Fastest" => CompressionLevel.Fastest, "NoCompression" => CompressionLevel.NoCompression, _ => CompressionLevel.Optimal };
                var started = time.GetTimestamp();
                _compressed = true;
                try { _output = encoding == "br" ? new BrotliStream(_measured, level, true) : new GZipStream(_measured, level, true); }
                finally { _codecTicks += time.GetElapsedTime(started).Ticks; }
            }
            else
            {
                _output = _measured;
            }

            if (_count > 0)
            {
                await WriteOutputAsync(_prefix!.AsMemory(0, _count), token);
            }

            _prefix = null; _count = 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_completed, this);
            if (buffer.IsEmpty)
            {
                return;
            }

            if (_output == null)
            {
                if (encoding == null || !CanCompress(context, config))
                {
                    await SelectAsync(false, cancellationToken);
                }
                else if (context.Response.ContentLength == null && config.MinResponseSize > 64 * 1024)
                {
                    await SelectAsync(false, cancellationToken);
                }
                else if (context.Response.ContentLength != null || (long)_count + buffer.Length >= config.MinResponseSize)
                {
                    await SelectAsync(true, cancellationToken);
                }
                else
                {
                    _prefix ??= new byte[Math.Max(1, config.MinResponseSize)];
                    buffer.CopyTo(_prefix.AsMemory(_count)); _count += buffer.Length; InputBytes += buffer.Length;
                    return;
                }
            }
            await WriteOutputAsync(buffer, cancellationToken);
            InputBytes += buffer.Length;
        }
        private async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            var started = time.GetTimestamp(); var destinationTicks = _measured.ElapsedTicks;
            try { await _output!.WriteAsync(buffer, token); }
            finally { AddCodecTime(started, destinationTicks); }
        }
        private void AddCodecTime(long started, long destinationTicks)
        {
            if (_compressed) _codecTicks += Math.Max(0, time.GetElapsedTime(started).Ticks - (_measured.ElapsedTicks - destinationTicks));
        }
        private async ValueTask DisposeOutputAsync()
        {
            var started = time.GetTimestamp(); var destinationTicks = _measured.ElapsedTicks;
            try { await _output!.DisposeAsync(); }
            finally { AddCodecTime(started, destinationTicks); }
        }
        public async Task CompleteAsync(CancellationToken token)
        {
            if (_completed)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            if (_output == null)
            {
                await SelectAsync(false, token);
            }

            _completed = true;
            if (_compressed)
            {
                await DisposeOutputAsync();
            }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            // Flush must not wait for the minimum size, including event streams.
            if (_output == null)
            {
                await SelectAsync(encoding != null && context.Response.ContentLength is > 0 && CanCompress(context, config), cancellationToken);
            }

            var started = time.GetTimestamp(); var destinationTicks = _measured.ElapsedTicks;
            try { await _output!.FlushAsync(cancellationToken); }
            finally { AddCodecTime(started, destinationTicks); }
        }
        public override void Flush() => FlushAsync(context.RequestAborted).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), context.RequestAborted).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        {
            _prefix = null;
            if (!_completed && _output != null && _compressed)
            {
                _completed = true;
                await DisposeOutputAsync();
            }
            GC.SuppressFinalize(this);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    // Counts completed application writes, including codec trailers. It never owns the destination.
    private sealed class MeasuredDestination(Stream destination, TimeProvider time) : Stream
    {
        public long Bytes { get; private set; }
        public long ElapsedTicks { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var started = time.GetTimestamp();
            try { await destination.WriteAsync(buffer, cancellationToken); Bytes += buffer.Length; }
            finally { ElapsedTicks += time.GetElapsedTime(started).Ticks; }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            var started = time.GetTimestamp();
            try { destination.Write(buffer, offset, count); Bytes += count; }
            finally { ElapsedTicks += time.GetElapsedTime(started).Ticks; }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush()
        {
            var started = time.GetTimestamp();
            try { destination.Flush(); } finally { ElapsedTicks += time.GetElapsedTime(started).Ticks; }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            var started = time.GetTimestamp();
            try { await destination.FlushAsync(cancellationToken); } finally { ElapsedTicks += time.GetElapsedTime(started).Ticks; }
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
