using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Wafish.Models;
using Wafish.Storage;

namespace Wafish.Filters;

/// <summary>Applies the shared response contract to configuration mutation actions.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ConfigurationMutationAttribute() : TypeFilterAttribute(typeof(ConfigurationMutationFilter));

internal sealed class ConfigurationMutationFilter(ILogger<ConfigurationMutationFilter> logger)
    : IAsyncAlwaysRunResultFilter, IAsyncExceptionFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static bool Applies(HttpContext context) => !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (Applies(context.HttpContext) && context.Result is ObjectResult result && result.Value is not ConfigurationMutationResponse)
        {
            var code = result.StatusCode ?? 200;
            var body = JsonSerializer.SerializeToElement(result.Value, JsonOptions);
            var details = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : body;
            string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
                && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
            long? Version(string key)
            {
                foreach (var value in new[] { body, details })
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var version)) return version;
                return null;
            }
            var outcome = Text(body, "outcome") ?? Text(body, "status") ?? Text(details, "status") ?? Outcome(code);
            var message = Text(body, "message") ?? Text(body, "title") ?? Outcome(code);
            var extra = body.ValueKind == JsonValueKind.Object
                ? body.EnumerateObject().Where(p => p.Name is not ("code" or "status" or "outcome" or "success" or "message" or "persistedVersion" or "activeVersion" or "type" or "title" or "detail"))
                    .ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
            result.Value = new ConfigurationMutationResponse { Status = code, Outcome = outcome, Success = code < 400,
                Message = message, PersistedVersion = Version("persistedVersion"), ActiveVersion = Version("activeVersion"), Data = extra };
        }
        if (Applies(context.HttpContext) && context.Result is ObjectResult { Value: ConfigurationMutationResponse response } normalized)
        {
            normalized.ContentTypes.Clear();
            normalized.ContentTypes.Add(response.Success ? "application/json" : "application/problem+json");
        }
        await next();
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (!Applies(context.HttpContext) || context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
            return Task.CompletedTask;
        logger.LogError(context.Exception, "Configuration mutation failed at {Path}", context.HttpContext.Request.Path);
        context.Result = context.Exception switch
        {
            ConfigurationUpdateException update => update.Result.ToHttpResult(),
            BindingRevisionConflictException => Failure(409, "VersionConflict", "Bindings changed. Reload before retrying."),
            ArgumentException => Failure(400, "ValidationRejected", "The mutation request is invalid."),
            _ => Failure(500, "InternalError", "The mutation failed. Inspect the current configuration before retrying.")
        };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    private static ObjectResult Failure(int code, string outcome, string message) => new(new ConfigurationMutationResponse
        { Status = code, Outcome = outcome, Success = false, Message = message }) { StatusCode = code };
    private static string Outcome(int code) => code switch
    {
        < 400 => "Applied", 400 or 422 => "ValidationRejected", 401 => "Unauthenticated", 403 => "PermissionDenied",
        404 => "NotFound", 409 => "VersionConflict", 503 => "StorageFailed", _ => "InternalError"
    };
}
