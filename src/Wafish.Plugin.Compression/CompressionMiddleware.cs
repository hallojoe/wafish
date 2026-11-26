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
    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) || !plans.Current.CompressionByRoute.TryGetValue(routeId, out var config) || !config.Enabled)
        { await next(context); return; }
        AppendVary(context.Response.Headers);
        context.Response.OnStarting(() => { AppendVary(context.Response.Headers); return Task.CompletedTask; });
        var encoding = SelectEncoding(context.Request.Headers.AcceptEncoding.ToArray(), config);
        var original = context.Response.Body;
        using var counted = new CompressionCountingStream(original);
        var completed = false;
        if (encoding == null || HttpMethods.IsHead(context.Request.Method) || context.Request.Headers.ContainsKey("Range") ||
            HasNoTransform(context.Request.Headers))
        {
            context.Response.Body = counted;
            try { await next(context); completed = true; }
            finally
            {
                context.Response.Body = original;
                CompressionMeasurements.Record(routeId, "identity", completed, counted.BytesWritten, counted.BytesWritten);
            }
            return;
        }
        var stream = new CompressionWriteStream(context, counted, config, encoding);
        context.Response.Body = stream;
        try { await next(context); await stream.CompleteAsync(context.RequestAborted); completed = true; }
        finally
        {
            context.Response.Body = original;
            try { await stream.DisposeAsync(); }
            finally { CompressionMeasurements.Record(routeId, stream.SelectedEncoding, completed, stream.InputBytes, counted.BytesWritten); }
        }
    }

    private static string? SelectEncoding(string?[] values, CompressionExecutionConfig config)
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
        var br = config.AllowedEncodings?.Contains("br", StringComparer.OrdinalIgnoreCase) == true ? Quality("br") : 0;
        var gzip = config.AllowedEncodings?.Contains("gzip", StringComparer.OrdinalIgnoreCase) == true ? Quality("gzip") : 0;
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

    private sealed class CompressionWriteStream(HttpContext context, Stream destination, CompressionExecutionConfig config, string encoding) : Stream
    {
        // Unknown-length responses never require more than 64 KiB of prefix storage.
        private byte[]? _prefix;
        private int _count;
        private Stream? _output;
        private bool _completed;
        public long InputBytes { get; private set; }
        public string SelectedEncoding { get; private set; } = "identity";

        private async ValueTask SelectAsync(bool compress, CancellationToken token)
        {
            if (compress)
            {
                SelectedEncoding = encoding;
                context.Response.Headers.ContentEncoding = encoding;
                context.Response.ContentLength = null;
                // A strong entity tag identifies the original representation only.
                var etag = context.Response.Headers.ETag.ToString();
                if (etag.Length > 0 && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.ETag = "W/" + etag;
                }

                var configuredLevel = (encoding == "br" ? config.BrotliCompressionLevel : config.GzipCompressionLevel) ?? config.CompressionLevel;
                var level = configuredLevel switch { "Fastest" => CompressionLevel.Fastest, "NoCompression" => CompressionLevel.NoCompression, _ => CompressionLevel.Optimal };
                _output = encoding == "br" ? new BrotliStream(destination, level, true) : new GZipStream(destination, level, true);
            }
            else
            {
                _output = destination;
            }

            if (_count > 0)
            {
                await _output.WriteAsync(_prefix!.AsMemory(0, _count), token);
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
                if (!CanCompress(context, config))
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
                    buffer.CopyTo(_prefix.AsMemory(_count)); _count += buffer.Length;
                    InputBytes += buffer.Length;
                    return;
                }
            }
            await _output!.WriteAsync(buffer, cancellationToken);
            InputBytes += buffer.Length;
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
            if (_output != destination)
            {
                await _output!.DisposeAsync();
            }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            // Flush must not wait for the minimum size, including event streams.
            if (_output == null)
            {
                await SelectAsync(context.Response.ContentLength is > 0 && CanCompress(context, config), cancellationToken);
            }

            await _output!.FlushAsync(cancellationToken);
        }
        public override void Flush() => FlushAsync(context.RequestAborted).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), context.RequestAborted).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        {
            _prefix = null;
            if (!_completed && _output != null && _output != destination)
            {
                _completed = true;
                await _output.DisposeAsync();
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
}
