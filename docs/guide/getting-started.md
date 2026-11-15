# Run your first gateway

This guide uses source code. Candidate NuGet packages have not been published.
Install .NET 10 and Git. Use Node 24 when building the administration UI or website.

## Get the source

```bash
git clone https://github.com/hallojoe/wafish.git
cd wafish
dotnet --version
```

## Start a small backend

Create an ASP.NET Core project in a separate terminal, outside the Wafish repository.
Keep it running while you test the gateway.

```bash
dotnet new web -n WafishDemoBackend
dotnet run --project WafishDemoBackend --urls http://127.0.0.1:5090
```

The generated backend answers `/` with `Hello World!`.

## Start the gateway

From the Wafish repository, start the runtime-only sample:

```bash
dotnet run --project samples/MinimalGateway
```

The gateway listens at `http://127.0.0.1:5080`.
The sample forwards `/api/` paths unchanged to port 5090.
To reach the generated backend root, add a path transform in `samples/MinimalGateway/appsettings.json`:

```json
"Transforms": [{ "PathRemovePrefix": "/api" }]
```

Add this property inside the existing route object, beside `ClusterId` and `Match`.
Preserve the rest of the sample configuration. Restart the sample after editing.

```bash
curl http://127.0.0.1:5080/api/
curl http://127.0.0.1:5080/health
```

The first response should contain `Hello World!`. The health endpoint reports gateway host readiness.
It does not prove that the backend is available.

## Understand the host

The runtime-only sample uses this composition:

```csharp
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
```

SQLite creates `minimal-gateway.db`. Use a durable, writable location for deployed data.
Built-in plugins are available through the runtime. Their bindings control where they apply.

## Add an administration workspace

Use [SampleGateway](../../samples/SampleGateway/README.md) for a complete composition.
For an orchestrated local environment, follow the [Aspire guide](../aspire.md).
Read the [security guide](../security.md) before exposing administration or registration endpoints.

The [React UI guide](../react-ui.md) explains hosting and prefix configuration.
The [architecture guide](../architecture.md) describes optional package boundaries.

## Common first-run failures

| Symptom | Check |
|---|---|
| Proxy failure | Start the backend and verify the destination address. |
| Backend returns 404 | Confirm the forwarded path and the path-removal transform. |
| Port already occupied | Stop the conflicting local process or change the sample configuration. |
| SQLite cannot open | Use a writable data directory and the correct encryption key, when configured. |
| UI build fails | Install Node and restore frontend dependencies through the project build. |

After package publication, use matching package versions in an independent host.
Until then, the source samples are the supported onboarding path described here.
