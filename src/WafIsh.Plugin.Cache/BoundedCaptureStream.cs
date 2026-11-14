namespace WafIsh.Plugin.Cache;

/// <summary>Streams every byte immediately. Capture stops and releases its budget on overflow.</summary>
internal sealed class BoundedCaptureStream(Stream destination, ResponseCacheStore.Capture capture) : Stream
{
    public int CapturedLength { get; private set; }
    public bool Complete => capture.Buffer != null;
    private void Capture(ReadOnlySpan<byte> data)
    {
        if (capture.Buffer is not { } buffer)
        {
            return;
        }

        if (data.Length > buffer.Length - CapturedLength) { capture.Dispose(); return; }
        data.CopyTo(buffer.AsSpan(CapturedLength));
        CapturedLength += data.Length;
    }
    public override void Write(byte[] buffer, int offset, int count)
    { destination.Write(buffer, offset, count); Capture(buffer.AsSpan(offset, count)); }
    public override void Write(ReadOnlySpan<byte> buffer) { destination.Write(buffer); Capture(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { await destination.WriteAsync(buffer, cancellationToken); Capture(buffer.Span); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
