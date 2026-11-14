using System.Text;
using WafIsh.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace WafIsh.Dashboard.Infrastructure.Auth;

/// <summary>Authorization filter for GatewayConfigController. Supports BasicAuth and ApiKey modes.</summary>
internal sealed class GatewayApiAuthFilter : IAsyncAuthorizationFilter
{
    private readonly GatewayApiAuthOptions _opts;
    public GatewayApiAuthFilter(IOptions<GatewayApiAuthOptions> options) => _opts = options.Value;

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var req = ctx.HttpContext.Request;
        var ok = WafIsh.Services.ControlPlaneCredentials.Validate(req.Headers, _opts);

        if (!ok)
            ctx.Result = new JsonResult(new { code = 401, message = "Unauthorized" }) { StatusCode = 401 };

        return Task.CompletedTask;
    }

}
