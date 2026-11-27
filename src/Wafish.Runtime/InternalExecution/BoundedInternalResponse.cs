using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Wafish.Runtime.InternalExecution;

internal sealed class BoundedInternalResponse : Stream, IHttpResponseFeature, IHttpResponseBodyFeature
{
    private readonly MemoryStream _buffer = new();
    private readonly int _limit;
    private readonly List<(Func<object,Task> Callback, object State)> _starting = [];
    private readonly List<(Func<object,Task> Callback, object State)> _completed = [];
    private PipeWriter? _writer;
    private bool _done;
    private bool _startingCallbacks;
    public BoundedInternalResponse(int limit) => _limit = limit;
    public bool LimitExceeded { get; private set; }
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get => this; set => throw new NotSupportedException(); }
    public bool HasStarted { get; private set; }
    public Stream Stream => this;
    public PipeWriter Writer => _writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen:true));
    public void OnStarting(Func<object,Task> callback,object state)
    { if (HasStarted) throw new InvalidOperationException("Response already started."); _starting.Add((callback,state)); }
    public void OnCompleted(Func<object,Task> callback,object state) => _completed.Add((callback,state));
    public void DisableBuffering() { }
    public async Task StartAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasStarted) return;
        if(_startingCallbacks) throw new InvalidOperationException("A starting callback cannot write the response.");
        _startingCallbacks=true;
        try { for(var i=_starting.Count-1;i>=0;i--) await _starting[i].Callback(_starting[i].State); HasStarted=true; }
        finally { _startingCallbacks=false; }
    }
    public async Task CompleteAsync()
    {
        if (_done) return;
        _done=true;
        List<Exception> errors=[];
        try { if (_writer!=null) await _writer.FlushAsync(); await StartAsync(); } catch(Exception error) { errors.Add(error); }
        for(var i=_completed.Count-1;i>=0;i--) { try { await _completed[i].Callback(_completed[i].State); } catch(Exception error) { errors.Add(error); } }
        if(errors.Count>0) throw new AggregateException(errors);
    }
    public async Task SendFileAsync(string path,long offset,long? count,CancellationToken cancellationToken=default)
    {
        await using var file=File.OpenRead(path); file.Position=offset;
        var remaining=count ?? file.Length-offset;
        var bytes=new byte[8192];
        while(remaining>0)
        {
            var read=await file.ReadAsync(bytes.AsMemory(0,(int)Math.Min(bytes.Length,remaining)),cancellationToken);
            if(read==0) break;
            await WriteAsync(bytes.AsMemory(0,read),cancellationToken); remaining-=read;
        }
    }
    public byte[] ToArray() => _buffer.ToArray();
    public override bool CanRead=>false;
    public override bool CanSeek=>false;
    public override bool CanWrite=>true;
    public override long Length=>_buffer.Length;
    public override long Position { get=>_buffer.Position; set=>throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken)=>StartAsync(cancellationToken);
    public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken)=>WriteAsync(buffer.AsMemory(offset,count),cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(buffer.Length>_limit-_buffer.Length) { LimitExceeded=true;throw new Wafish.Services.InternalRouteException(502,"response_limit"); }
        if (_buffer.Capacity < _buffer.Length + buffer.Length) _buffer.Capacity = Math.Min(_limit, Math.Max((int)_buffer.Length + buffer.Length, Math.Max(256, _buffer.Capacity * 2)));
        await StartAsync(cancellationToken);
        await _buffer.WriteAsync(buffer,cancellationToken);
    }
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if(disposing) { try { _writer?.Complete(); } finally { _buffer.Dispose(); } } base.Dispose(disposing); }
}
