using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Wafish.Services;

namespace Wafish.Plugin.Aggregation;

public sealed class AggregationHandler(IInternalRouteExecutor executor,AggregationAdmission admission)
{
    private static readonly Meter Meter=new("Wafish.Plugin.Aggregation");
    private static readonly Counter<long> Results=Meter.CreateCounter<long>("wafish.aggregation.requests");
    public async Task InvokeAsync(HttpContext context,CompiledAggregation config)
    {
        context.Response.Headers.CacheControl="no-store";
        if(context.Request.ContentLength>0 || context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody==true) {Fail(context,400);return;}
        using var reservation=admission.TryAcquire(2L*config.Children.Length*config.MaxChildBytes);
        if(reservation==null) {Fail(context,503);return;}
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var bodies=new byte[]?[config.Children.Length];
        try
        {
            var calls=AggregationCompiler.Resolve(config,context);
            await using(var batch=await executor.PrepareAsync(context,calls,config.MaxChildBytes,deadline.Token))
            {
                using var slots=new SemaphoreSlim(config.Parallelism);
                var total=0;Exception? failure=null;
                async Task Execute(int index)
                {
                    var acquired=false;
                    try
                    {
                        await slots.WaitAsync(deadline.Token);acquired=true;
                        var response=await batch.ExecuteAsync(index,deadline.Token);
                        if(response.StatusCode is 401 or 403 or 429) throw new InternalRouteException(response.StatusCode,"child_policy");
                        if(response.StatusCode==504) throw new InternalRouteException(504,"child_timeout");
                        if(response.StatusCode<200 || response.StatusCode>=300 || !string.IsNullOrEmpty(response.ContentEncoding) && !response.ContentEncoding.Equals("identity",StringComparison.OrdinalIgnoreCase))
                            throw new InternalRouteException(502,"child_response");
                        if(Interlocked.Add(ref total,response.Body.Length)>config.MaxCombinedBytes) throw new InternalRouteException(502,"combined_response_limit");
                        if(response.StatusCode==204) return;
                        if(!ValidJsonType(response.ContentType))
                            throw new InternalRouteException(502,"child_content_type");
                        using var document=JsonDocument.Parse(response.Body,new JsonDocumentOptions {MaxDepth=64});
                        bodies[index]=response.Body;
                    }
                    catch(Exception error)
                    {
                        Interlocked.CompareExchange(ref failure,error,null);
                        await deadline.CancelAsync();
                        throw;
                    }
                    finally {if(acquired) slots.Release();}
                }
                var tasks=Enumerable.Range(0,calls.Length).Select(Execute).ToArray();
                try {await Task.WhenAll(tasks);}
                catch {throw failure ?? new InternalRouteException(502,"child_failed");}
            }
            deadline.Token.ThrowIfCancellationRequested();
            context.Response.ContentType="application/json";
            using var writer=new Utf8JsonWriter(context.Response.BodyWriter);
            writer.WriteStartObject();
            for(var i=0;i<bodies.Length;i++)
            {
                writer.WritePropertyName(config.Children[i].Name);
                if(bodies[i]==null) writer.WriteNullValue();else writer.WriteRawValue(bodies[i]!,skipInputValidation:true);
                writer.Flush();await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
            }
            writer.WriteEndObject();writer.Flush();await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
            Results.Add(1,new KeyValuePair<string,object?>("status",200));
        }
        catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested) {throw;}
        catch(OperationCanceledException) {Fail(context,504);}
        catch(InternalRouteException error) {Fail(context,error.StatusCode);}
        catch(Exception) when(!context.Response.HasStarted) {Fail(context,502);}
    }
    private static bool ValidJsonType(string? contentType)
    {
        if(!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType,out var parsed)) return false;
        var charset=parsed.CharSet?.Trim('"');
        if(!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8",StringComparison.OrdinalIgnoreCase)) return false;
        var type=parsed.MediaType;
        return type!=null && (type.Equals("application/json",StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/",StringComparison.OrdinalIgnoreCase) && type.EndsWith("+json",StringComparison.OrdinalIgnoreCase));
    }
    private static void Fail(HttpContext context,int status)
    {
        if(context.Response.HasStarted) {context.Abort();return;}
        context.Response.StatusCode=status;context.Response.ContentType=null;context.Response.ContentLength=0;
        Results.Add(1,new KeyValuePair<string,object?>("status",status));
    }
}
