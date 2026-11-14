using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Filters;
using Wafish.Models;
using Wafish.Storage;

internal static class MutationResponseChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Mutation responses: all configuration outcomes share HTTP status, outcome, and revision fields", async () =>
        {
            foreach (var (status, code) in new[] { (ConfigurationUpdateStatus.Applied, 200), (ConfigurationUpdateStatus.ValidationRejected, 400),
                (ConfigurationUpdateStatus.VersionConflict, 409), (ConfigurationUpdateStatus.StorageFailed, 503), (ConfigurationUpdateStatus.ActivationFailed, 500) })
            {
                var result = new RouteOperationResult(status == ConfigurationUpdateStatus.Applied, "fixture")
                    { Status = status, PersistedVersion = 12, ActiveVersion = status == ConfigurationUpdateStatus.ActivationFailed ? 11 : 12 }.ToHttpResult(new { routeId = "fixture" });
                var response = (ConfigurationMutationResponse)result.Value!;
                Check(result.StatusCode == code && response.Status == code && response.Outcome == status.ToString()
                    && response.PersistedVersion == 12 && response.ActiveVersion.HasValue, "Core result lost its outcome.");
                Check(result.ContentTypes.Single() == (response.Success ? "application/json" : "application/problem+json"), "Error media type is inconsistent.");
            }
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var filter = (IAsyncAlwaysRunResultFilter)new ConfigurationMutationAttribute().CreateInstance(services);
            foreach (var code in new[] { 200, 400, 401, 403, 404, 409, 422, 500, 503 })
            {
                var action = Context();
                var result = new ObjectResult(new { code, message = "fixture", data = new { status = code == 503 ? "StorageFailed" : (string?)null,
                    persistedVersion = (long?)12, activeVersion = (long?)11 }, routeId = "legacy-field" }) { StatusCode = code };
                var executing = new ResultExecutingContext(action, [], result, new object());
                await filter.OnResultExecutionAsync(executing, () => Task.FromResult(new ResultExecutedContext(action, [], result, new object())));
                var response = (ConfigurationMutationResponse)result.Value!;
                Check(response.Status == code && response.PersistedVersion == 12 && response.ActiveVersion == 11 && response.Data["routeId"].GetString() == "legacy-field",
                    "Legacy response normalization lost fields.");
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                Check(json.RootElement.GetProperty("status").GetInt32() == code && json.RootElement.TryGetProperty("outcome", out _), "JSON status collides with the application outcome.");
            }
        });
        await run("Mutation responses: safe exceptions preserve typed failures and caller cancellation", async () =>
        {
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var filter = (IAsyncExceptionFilter)new ConfigurationMutationAttribute().CreateInstance(services);
            foreach (var (exception, expected) in new (Exception, string)[]
            {
                (new ArgumentException("fixture-secret"), "ValidationRejected"),
                (new BindingRevisionConflictException(1, 2), "VersionConflict"),
                (new IOException("fixture-secret"), "InternalError"),
                (new ConfigurationUpdateException(new(false, "safe") { Status = ConfigurationUpdateStatus.ActivationFailed, PersistedVersion = 3, ActiveVersion = 2 }), "ActivationFailed")
            })
            {
                var context = new ExceptionContext(Context(), []) { Exception = exception };
                await filter.OnExceptionAsync(context);
                var body = (ConfigurationMutationResponse)((ObjectResult)context.Result!).Value!;
                Check(context.ExceptionHandled && body.Outcome == expected && !body.Message.Contains("fixture-secret"), "Exception mapping hid state or exposed details.");
            }
            var cancelled = Context(); cancelled.HttpContext.RequestAborted = new CancellationToken(true);
            var cancellation = new ExceptionContext(cancelled, []) { Exception = new OperationCanceledException(cancelled.HttpContext.RequestAborted) };
            await filter.OnExceptionAsync(cancellation);
            Check(!cancellation.ExceptionHandled, "Caller cancellation became a mutation failure response.");
            var read = Context(); read.HttpContext.Request.Method = "GET";
            var readFailure = new ExceptionContext(read, []) { Exception = new IOException() };
            await filter.OnExceptionAsync(readFailure);
            Check(!readFailure.ExceptionHandled, "Read endpoint entered the mutation contract.");
        });
    }
    private static ActionContext Context()
    {
        var http = new DefaultHttpContext(); http.Request.Method = "PUT"; http.Request.Path = "/fixture";
        return new(http, new RouteData(), new ActionDescriptor());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
