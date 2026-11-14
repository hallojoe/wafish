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
        return new ObjectResult(new { code, result.Success, result.Message, result.Status,
            result.PersistedVersion, result.ActiveVersion, data }) { StatusCode = code };
    }
}
