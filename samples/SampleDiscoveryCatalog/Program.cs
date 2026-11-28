using SampleDiscoveryCatalog;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var port = builder.Configuration.GetValue<int>("CATALOG_HTTP_PORT");
if (port is < 1 or > 65535) throw new InvalidOperationException("Set CATALOG_HTTP_PORT to an assigned local port.");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddDiscoveryCatalog(new DiscoveryCatalog(builder.Configuration.GetSection("Catalog:Backends").Get<Dictionary<string, string>>() ?? []));
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapDiscoveryCatalog();
app.MapDefaultEndpoints();
app.MapOpenApi();
await app.RunAsync();
