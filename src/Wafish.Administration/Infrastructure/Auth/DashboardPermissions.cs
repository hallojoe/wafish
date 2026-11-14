using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Wafish.Services;

namespace Wafish.Dashboard.Infrastructure.Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DashboardPermission { Read, ConfigurationWrite, PluginManagement, CredentialManagement }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DashboardPermissionAttribute(DashboardPermission permission) : Attribute
{
    public DashboardPermission Permission { get; } = permission;
}

public interface IDashboardPermissionService
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, DashboardPermission permission, string operation, CancellationToken cancellationToken = default);
}

/// <summary>Operation checks run after authentication. Custom rules and authenticated claims can restrict the shared credential policy.</summary>
public sealed class DashboardPermissionService(IOptions<DashboardOptions> options, IConfigChangeAuditLog audit) : IDashboardPermissionService
{
    public const string PermissionClaim = "wafish.permission";
    internal const string ActorKey = "Wafish.Administration.Actor";

    public async ValueTask<bool> IsAllowedAsync(HttpContext context, DashboardPermission permission, string operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = options.Value.Auth;
        var claims = context.User.Identity?.IsAuthenticated == true ? context.User.FindAll(PermissionClaim).ToArray() : [];
        var allowed = policy.AuthorizeOperation is not null
            ? await policy.AuthorizeOperation(context, permission, cancellationToken)
            : claims.Length > 0 ? claims.Any(c => c.Value == permission.ToString()) : policy.Permissions is null || policy.Permissions.Contains(permission);
        if (!allowed)
        {
            var actor = Actor(context);
            audit.RecordFailure("PermissionDenied", operation, "Required permission: " + permission, actor[..Math.Min(actor.Length, 256)]);
        }
        return allowed;
    }

    public static string Actor(HttpContext context)
    {
        var actor = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : context.Items[ActorKey] as string;
        return string.IsNullOrWhiteSpace(actor) ? "authenticated-caller" : actor;
    }

    internal static DashboardPermission Required(ControllerActionDescriptor action, string method)
    {
        var explicitRule = action.MethodInfo.GetCustomAttributes(typeof(DashboardPermissionAttribute), true).Cast<DashboardPermissionAttribute>().FirstOrDefault()
            ?? action.ControllerTypeInfo.GetCustomAttributes(typeof(DashboardPermissionAttribute), true).Cast<DashboardPermissionAttribute>().FirstOrDefault();
        if (explicitRule is not null) return explicitRule.Permission;
        if (action.ControllerName == "DashboardAuth" && action.ActionName != "GetAuthStatus") return DashboardPermission.CredentialManagement;
        if (action.ControllerName == "DashboardSettings" && action.ActionName is "DownloadDatabase" or "BackupDatabase") return DashboardPermission.CredentialManagement;
        if (action.ControllerName == "AI" && action.ActionName is "GetConfig" or "SaveConfig" or "TestConnection") return DashboardPermission.CredentialManagement;
        if (action.ControllerName == "WebhookSettings" && action.ActionName is "GetSettings" or "SaveSettings" or "SavePlatform" or "Test") return DashboardPermission.CredentialManagement;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return DashboardPermission.Read;
        return action.ControllerName.Contains("Plugin", StringComparison.Ordinal) || action.ControllerName == "Presets"
            || action.ControllerTypeInfo.Namespace?.StartsWith("Wafish.Plugin.", StringComparison.Ordinal) == true
            ? DashboardPermission.PluginManagement : DashboardPermission.ConfigurationWrite;
    }
}
