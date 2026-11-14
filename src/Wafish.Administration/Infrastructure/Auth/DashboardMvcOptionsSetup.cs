using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>
/// Configures MVC conventions and filters for the Dashboard:
/// - Resolves JWT secret via <see cref="JwtSecretProvider"/>
/// - Adds <see cref="DashboardRouteConvention"/> for route prefix
/// - Adds <see cref="DashboardAuthFilter"/> when auth is enabled
/// </summary>
internal sealed class DashboardMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    private readonly IOptions<DashboardOptions> _dashboardOptions;
    private readonly JwtSecretProvider _jwtSecretProvider;
    private readonly IDashboardAuthorizationService _authService;

    public DashboardMvcOptionsSetup(
        IOptions<DashboardOptions> dashboardOptions,
        JwtSecretProvider jwtSecretProvider,
        IDashboardAuthorizationService authService)
    {
        _dashboardOptions = dashboardOptions;
        _jwtSecretProvider = jwtSecretProvider;
        _authService = authService;
    }

    public void Configure(MvcOptions mvcOptions)
    {
        var opts = _dashboardOptions.Value;
        var prefix = opts.RoutePrefix.Trim('/');

        opts.Auth.JwtSecret = _jwtSecretProvider.GetSecret(opts.Auth.JwtSecret);

        mvcOptions.Conventions.Add(new DashboardRouteConvention(prefix));

        {
            var authFilter = new DashboardAuthFilter(_authService);
            mvcOptions.Filters.Add(authFilter);
        }
    }
}
