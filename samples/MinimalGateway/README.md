# Minimal gateway

Run `dotnet run --project samples/MinimalGateway` from the repository root.
The gateway listens on `http://127.0.0.1:5080`. `/health` reports host readiness.
Requests under `/api/` forward unchanged to `http://127.0.0.1:5090/`.
Start your backend there, or set `ReverseProxy__Clusters__backend__Destinations__one__Address`.
A missing backend returns a proxy failure. Readiness does not check backend availability.

The only direct dependencies are `Wafish.Runtime` and `Wafish.Storage.Sqlite`.
Use matching NuGet package references instead of project references in an independent application.
SQLite creates `minimal-gateway.db`. Supply a durable, writable location for deployment.
This sample has no administration UI, AI, service registration, or Aspire dependency.
Runtime includes built-in plugin implementations; bindings control their use.

Add `Wafish.Administration` with `AddWafishAdministration` and `MapWafishAdministration` for administration.
Configure authentication before exposing administration endpoints.
Add `Wafish.ReactUi` with `AddWafishReactUi` and `MapWafishReactUi` for the optional console.
See [SampleGateway](../SampleGateway/README.md) for the complete composition.
