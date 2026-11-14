using WafIsh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WafIsh.Dashboard.Infrastructure.Auth;

/// <summary>
/// Async auth filter for Dashboard controllers.
/// Applies to all controllers under the WafIsh.Dashboard namespace.
/// Skips login/logout actions. Returns HTTP 401 for unauthorized API calls.
/// Uses IDashboardAuthorizationService for unified authorization check.
/// </summary>
internal sealed class DashboardAuthFilter : IAsyncAuthorizationFilter
{
    private readonly IDashboardAuthorizationService _authService;

    public DashboardAuthFilter(IDashboardAuthorizationService authService)
    {
        _authService = authService;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerTypeInfo: var ci })
            return;

        if (!ci.Namespace?.StartsWith("WafIsh.Dashboard.", StringComparison.Ordinal) == true)
            return;

        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value;
        var policy = context.HttpContext.RequestServices.GetRequiredService<ControlPlaneAccessPolicy>();
        if (!policy.IsAllowed(context.HttpContext, options.Auth.AuthMode == DashboardAuthMode.None && options.Auth.AuthorizeRequest == null))
        {
            context.Result = new JsonResult(new { code = 403, message = "Administration requires HTTPS or an allowed direct loopback connection." }) { StatusCode = 403 };
            return;
        }

        // Skip login/logout actions - they are public
        var actionName = ((ControllerActionDescriptor)context.ActionDescriptor).ActionName;
        if (string.Equals(actionName, "Login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "Logout", StringComparison.OrdinalIgnoreCase))
            return;

        // Use authorization service for unified check
        if (await _authService.IsAuthorizedAsync(context.HttpContext)) return;

        if (context.HttpContext.Response.StatusCode == 429)
        {
            context.Result = new JsonResult(new { code = 429, message = "Too many authentication attempts. Retry later." }) { StatusCode = 429 };
            return;
        }
        if (options.Auth.AuthMode == DashboardAuthMode.BasicAuth)
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"Gateway administration\", charset=\"UTF-8\"";
            context.Result = new UnauthorizedResult();
        }
        else
            context.Result = new JsonResult(new { code = 401, message = "Unauthorized" }) { StatusCode = 401 };
    }
}
