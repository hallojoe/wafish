using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Wafish.Plugin.Compression;

internal static class CompressionMeasurements
{
    private static readonly Meter Meter = new("Wafish.Plugin.Compression", "1.0");
    private static readonly Counter<long> Input = Meter.CreateCounter<long>("wafish.compression.input_bytes", "By");
    private static readonly Counter<long> Output = Meter.CreateCounter<long>("wafish.compression.output_bytes", "By");
    private static readonly Counter<long> Responses = Meter.CreateCounter<long>("wafish.compression.responses", "{response}");
    private static readonly ConcurrentDictionary<string, string> Routes = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static string Route(string id)
    {
        if (Routes.TryGetValue(id, out var existing)) return existing;
        lock (Gate)
        {
            if (Routes.TryGetValue(id, out existing)) return existing;
            if (Routes.Count >= 128) return "__other__";
            Routes[id] = id;
            return id;
        }
    }
    public static void Record(string route, string encoding, bool completed, long input, long output)
    {
        if (!Input.Enabled && !Output.Enabled && !Responses.Enabled) return;
        var tags = new TagList
        {
            { "wafish.route.id", Route(route) },
            { "compression.encoding", encoding },
            { "compression.outcome", completed ? "completed" : "aborted" }
        };
        Input.Add(input, tags); Output.Add(output, tags); Responses.Add(1, tags);
    }
}

internal sealed class CompressionCountingStream(Stream destination) : Stream
{
    public long BytesWritten { get; private set; }
    public override void Write(byte[] buffer, int offset, int count) { destination.Write(buffer, offset, count); BytesWritten += count; }
    public override void Write(ReadOnlySpan<byte> buffer) { destination.Write(buffer); BytesWritten += buffer.Length; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await destination.WriteAsync(buffer, cancellationToken); BytesWritten += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
