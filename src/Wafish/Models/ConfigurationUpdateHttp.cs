using Microsoft.AspNetCore.Mvc;
namespace Wafish.Models;

public static class ConfigurationUpdateHttp
{
    public static int StatusCode(ConfigurationUpdateStatus status) => status switch
    {
        ConfigurationUpdateStatus.Applied => 200,
        ConfigurationUpdateStatus.ValidationRejected => 400,
        ConfigurationUpdateStatus.VersionConflict => 409,
        ConfigurationUpdateStatus.StorageFailed => 503,
        _ => 500
    };

    public static ObjectResult ToHttpResult(this RouteOperationResult result, object? data = null)
    {
        var code = StatusCode(result.Status);
        return new ObjectResult(new ConfigurationMutationResponse
        {
            Status = code, Outcome = result.Status.ToString(), Success = result.Success, Message = result.Message,
            PersistedVersion = result.PersistedVersion, ActiveVersion = result.ActiveVersion,
            Data = new() { ["data"] = System.Text.Json.JsonSerializer.SerializeToElement(data) }
        }) { StatusCode = code, ContentTypes = { result.Success ? "application/json" : "application/problem+json" } };
    }
}
