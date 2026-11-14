using Wafish.Runtime;
using Wafish.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWafishStorage();
builder.Services.AddWafishRuntime();
var app = builder.Build();
app.UseWafishRuntime();
app.MapWafishProxy();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
await app.RunAsync();
