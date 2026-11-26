using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SampleDiscoveryCatalog;

public static class CatalogApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args); configure?.Invoke(builder);
        var port = builder.Configuration.GetValue<int>("CATALOG_HTTP_PORT");
        var key = builder.Configuration["Catalog:ControlKey"] ?? "";
        if (port is < 0 or > 65535 || key.Length < 32) throw new ArgumentException("A valid port and private control key are required.");
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "backend-one", "backend-two" })
        {
            if (!Uri.TryCreate(builder.Configuration["Catalog:Backends:" + name], UriKind.Absolute, out var target) || target.Scheme != "http" ||
                !(target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(target.Host, out var ip) && IPAddress.IsLoopback(ip)) ||
                target.Port == 0 || target.AbsolutePath != "/" || target.Query.Length != 0 || target.Fragment.Length != 0 || target.UserInfo.Length != 0)
                throw new ArgumentException("Catalog backends must be explicit loopback HTTP origins.");
            addresses.Add(name, target.AbsoluteUri);
        }
        if (addresses.Values.Distinct(StringComparer.Ordinal).Count() != 2) throw new ArgumentException("Catalog backends must be distinct.");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 4096; server.Limits.MaxConcurrentConnections = 128; });
        var app = builder.Build(); var state = new CatalogState(addresses);
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path.StartsWithSegments("/__catalog") && !CryptographicOperations.FixedTimeEquals(expected,
                SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Catalog-Key"].ToString()))))
            { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/catalog", () => state.Read());
        app.MapGet("/__catalog", () => Results.Ok(state.Snapshot()));
        app.MapPost("/__catalog", (CatalogRequest request) => state.Configure(request) ? Results.Ok(state.Snapshot()) : Results.BadRequest());
        return app;
    }
}
public sealed record CatalogRequest(string[] Instances, bool Unavailable = false);
public sealed class CatalogState(IReadOnlyDictionary<string, string> addresses)
{
    private readonly object _gate = new();
    private string[] _instances = ["backend-one"];
    private bool _unavailable;
    private long _requests;
    public bool Configure(CatalogRequest request)
    {
        if (request.Instances is null || request.Instances.Length > 2 || request.Instances.Any(name => name is null || !addresses.ContainsKey(name)) ||
            request.Instances.Distinct(StringComparer.Ordinal).Count() != request.Instances.Length) return false;
        lock (_gate) { _instances = request.Instances.Order(StringComparer.Ordinal).ToArray(); _unavailable = request.Unavailable; }
        return true;
    }
    public IResult Read()
    {
        lock (_gate)
        {
            _requests++;
            return _unavailable ? Results.StatusCode(503) : Results.Ok(_instances.Select(name => addresses[name]).ToArray());
        }
    }
    public object Snapshot() { lock (_gate) { return new { instances = _instances.ToArray(), unavailable = _unavailable, requests = _requests }; } }
}
