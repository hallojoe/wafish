using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Wafish.Plugin.ExternalLogin;

public static class ExternalLoginExtensions
{
    /// <summary>Attaches the visitor cookie scheme without changing host defaults.</summary>
    public static void ConfigureEndpoint(Microsoft.AspNetCore.Builder.EndpointBuilder endpoint)
    {
        var route = endpoint.Metadata.OfType<Yarp.ReverseProxy.Model.RouteModel>().FirstOrDefault()?.Config;
        var binding = ExternalLoginCompiler.Read(route);
        if (binding != null)
            endpoint.Metadata.Add(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(ExternalLoginCatalog.CookieScheme(binding.Site)).RequireAuthenticatedUser().Build());
    }

    public static IServiceCollection AddWafishExternalLogin(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (services.Any(d => d.ServiceType == typeof(ExternalLoginCatalog))) throw new InvalidOperationException("External login is already registered.");
        var options = configuration.GetSection("Gateway:ExternalLogin").Get<ExternalLoginOptions>() ?? throw new ArgumentException("External login settings are required.");
        var catalog = new ExternalLoginCatalog(options);
        services.AddSingleton(catalog);
        services.TryAddSingleton(TimeProvider.System);
        // Use an independent provider. Administration cookies retain their existing protection configuration.
        services.AddSingleton<RedisVisitorSessionStore>(sp =>
        {
            var protection = DataProtectionProvider.Create(new DirectoryInfo(options.KeyRingPath), builder =>
            {
                builder.SetApplicationName(options.Namespace);
                if (!string.IsNullOrWhiteSpace(options.KeyCertificatePath))
                    builder.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(options.KeyCertificatePath,
                        options.KeyCertificatePasswordReference == null ? null : configuration[options.KeyCertificatePasswordReference]));
                else if (!environment.IsDevelopment()) throw new InvalidOperationException("Production visitor keys require an encryption certificate.");
            });
            return new RedisVisitorSessionStore(Required(configuration, options.RedisConnectionReference), catalog, protection, sp.GetRequiredService<TimeProvider>());
        });
        // Separate protection for OIDC state and cookie handles; all replicas use this key ring.
        var authenticationProtection = DataProtectionProvider.Create(new DirectoryInfo(options.KeyRingPath), builder =>
        {
            builder.SetApplicationName(options.Namespace);
            if (!string.IsNullOrWhiteSpace(options.KeyCertificatePath))
                builder.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(options.KeyCertificatePath,
                    options.KeyCertificatePasswordReference == null ? null : configuration[options.KeyCertificatePasswordReference]));
            else if (!environment.IsDevelopment()) throw new InvalidOperationException("Production visitor keys require an encryption certificate.");
        });
        var authentication = services.AddAuthentication();
        foreach (var site in catalog.Options.Sites)
        {
            var scheme = ExternalLoginCatalog.CookieScheme(site.Id);
            authentication.AddCookie(scheme, cookie =>
            {
                cookie.Cookie.Name = "__Host-wafish-visitor-" + site.Id;
                cookie.Cookie.Path = "/"; cookie.Cookie.HttpOnly = true; cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(site.AbsoluteMinutes); cookie.SlidingExpiration = false;
                cookie.DataProtectionProvider = authenticationProtection;
                cookie.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
            services.AddOptions<CookieAuthenticationOptions>(scheme).Configure<RedisVisitorSessionStore, TimeProvider>((cookie, store, clock) => { cookie.SessionStore = store; cookie.TimeProvider = clock; });
            foreach (var provider in site.Providers)
            {
                authentication.AddOpenIdConnect(ExternalLoginCatalog.OidcScheme(site.Id, provider.Id), oidc =>
                {
                    oidc.Authority = provider.Authority; oidc.ClientId = provider.ClientId;
                    oidc.ClientSecret = Required(configuration, provider.ClientSecretReference);
                    oidc.SignInScheme = scheme; oidc.CallbackPath = ExternalLoginCatalog.Callback(site.Id, provider.Id);
                    oidc.ResponseType = OpenIdConnectResponseType.Code; oidc.UsePkce = true; oidc.SaveTokens = true;
                    oidc.TokenValidationParameters.NameClaimType = "name";
                    oidc.MapInboundClaims = false; oidc.GetClaimsFromUserInfoEndpoint = false;
                    oidc.DataProtectionProvider = authenticationProtection;
                    oidc.Scope.Clear(); foreach (var scope in provider.Scopes) oidc.Scope.Add(scope);
                    oidc.Events.OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri = site.Origin + oidc.CallbackPath;
                        context.Properties.Items[RedisVisitorSessionStore.SiteKey] = site.Id;
                        context.Properties.Items[RedisVisitorSessionStore.ProviderKey] = provider.Id;
                        context.Properties.Items[".wafish-profile"] = catalog.Stamp(site.Id);
                        return Task.CompletedTask;
                    };
                    oidc.Events.OnPushAuthorization = context =>
                    {
                        context.ProtocolMessage.RedirectUri = site.Origin + oidc.CallbackPath;
                        return Task.CompletedTask;
                    };
                    oidc.Events.OnTokenResponseReceived = context =>
                    {
                        context.Properties!.Items[".wafish-granted-scopes"] = context.TokenEndpointResponse.Scope ?? string.Join(' ', provider.Scopes);
                        if (!string.IsNullOrEmpty(context.TokenEndpointResponse.AccessToken) && !string.Equals(context.TokenEndpointResponse.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                            context.Fail("Unsupported access token type.");
                        return Task.CompletedTask;
                    };
                    oidc.Events.OnMessageReceived = context =>
                    {
                        if (ExternalLoginRuntime.Origin(context.Request) != site.Origin) context.Fail("Invalid authentication origin.");
                        return Task.CompletedTask;
                    };
                    oidc.Events.OnTicketReceived = context =>
                    {
                        if (context.Properties?.Items.GetItem(RedisVisitorSessionStore.SiteKey) != site.Id ||
                            context.Properties.Items.GetItem(RedisVisitorSessionStore.ProviderKey) != provider.Id ||
                            context.Properties.Items.GetItem(".wafish-profile") != catalog.Stamp(site.Id) ||
                            !ExternalLoginRuntime.LocalReturn(context.ReturnUri)) context.Fail("Invalid authentication session.");
                        else ExternalLoginRuntime.Count("login_succeeded");
                        return Task.CompletedTask;
                    };
                    oidc.Events.OnRemoteFailure = async context =>
                    {
                        ExternalLoginRuntime.Count("login_failed");
                        context.HandleResponse(); context.Response.StatusCode = 400;
                        context.Response.Headers.CacheControl = "no-store";
                        await context.Response.WriteAsync("Authentication failed. Start a new login.");
                    };
                });
            }
        }
        services.AddHttpClient("Wafish.ExternalLogin.Tokens", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton(sp => new ExternalLoginRuntime(catalog, sp.GetRequiredService<RedisVisitorSessionStore>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>(), sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
    private static string Required(IConfiguration configuration, string reference) => !string.IsNullOrWhiteSpace(configuration[reference])
        ? configuration[reference]! : throw new ArgumentException("Missing external login secret configuration.");
}
