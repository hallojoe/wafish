using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Wafish.Plugin.Retry;

/// <summary>Buffers only retryable failures. Flush, start, completion, and overflow commit the response.</summary>
internal sealed class RetryResponseBody : Stream, IHttpResponseBodyFeature
{
    internal const int Limit = 64 * 1024;
    private readonly HttpContext _context;
    private readonly IHttpResponseBodyFeature _original;
    private readonly Func<int, bool> _canBuffer;
    private byte[]? _buffer;
    private int _count;
    private PipeWriter? _writer;
    private bool _bufferingDisabled;
    public bool Committed { get; private set; }

    public RetryResponseBody(HttpContext context, IHttpResponseBodyFeature original, Func<int, bool> canBuffer)
        => (_context, _original, _canBuffer) = (context, original, canBuffer);

    Stream IHttpResponseBodyFeature.Stream => this;
    public PipeWriter Writer => _writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));

    public async Task CommitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Committed = true;
        if (_count > 0)
        {
            await _original.Stream.WriteAsync(_buffer!.AsMemory(0, _count), token);
            _count = 0;
        }
        _buffer = null;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty)
        {
            return;
        }

        if (!Committed && !_bufferingDisabled && !_context.Response.HasStarted && _canBuffer(_context.Response.StatusCode) && bytes.Length <= Limit - _count)
        {
            _buffer ??= new byte[Limit];
            bytes.CopyTo(_buffer.AsMemory(_count));
            _count += bytes.Length;
            return;
        }
        await CommitAsync(cancellationToken);
        await _original.Stream.WriteAsync(bytes, cancellationToken);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await CommitAsync(cancellationToken);
        await _original.Stream.FlushAsync(cancellationToken);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await CommitAsync(cancellationToken);
        await _original.StartAsync(cancellationToken);
    }
    public async Task FinishAsync(CancellationToken token)
    {
        if (_writer != null)
        {
            await _writer.FlushAsync(token);
        }
    }

    public async Task CompleteAsync()
    {
        if (_writer != null)
        {
            await _writer.FlushAsync(_context.RequestAborted);
        }

        await CommitAsync(_context.RequestAborted);
        await _original.CompleteAsync();
    }
    public void DisableBuffering()
    {
        // Commit on the next asynchronous write or flush, including any retained prefix.
        _bufferingDisabled = true;
        _original.DisableBuffering();
    }
    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        await CommitAsync(cancellationToken);
        await _original.SendFileAsync(path, offset, count, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _writer?.Complete(new OperationCanceledException("Retry capture ended")); _buffer = null; }
        base.Dispose(disposing);
    }
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), _context.RequestAborted).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => FlushAsync(_context.RequestAborted).GetAwaiter().GetResult();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
