using System.Text;
using WafIsh.Controllers;
using WafIsh.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace WafIsh.Services;

internal sealed class GatewayApiAuthFilter : IAsyncAuthorizationFilter
{
    private readonly GatewayApiAuthOptions _options;

    private readonly ControlPlaneAccessPolicy _policy;
    public GatewayApiAuthFilter(IOptions<GatewayApiAuthOptions> options, ControlPlaneAccessPolicy policy)
    { _options = options.Value; _policy = policy; }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerTypeInfo: var typeInfo } ||
            typeInfo.AsType() != typeof(GatewayConfigController))
            return Task.CompletedTask;

        if (!_policy.IsAllowed(context.HttpContext, _options.Mode == GatewayApiAuthMode.None))
        {
            context.Result = new JsonResult(new { code = 403, message = "Administration requires HTTPS or an allowed direct loopback connection." }) { StatusCode = 403 };
            return Task.CompletedTask;
        }
        var request = context.HttpContext.Request;
        var ok = ControlPlaneCredentials.Validate(request.Headers, _options);

        if (!ok)
            context.Result = new JsonResult(new { code = 401, message = "Unauthorized" }) { StatusCode = StatusCodes.Status401Unauthorized };

        return Task.CompletedTask;
    }

}
