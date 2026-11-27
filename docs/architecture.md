# Gateway package boundaries

Historical decisions and extraction evidence are in [ARCHIVE.md](../ARCHIVE.md#archive-docs-architecture-md).

## Ownership

| Package | Responsibility | Dependencies |
|---|---|---|
| Wafish | Configuration, snapshots, registration protocols, and core contracts | Storage and plugin abstractions; client and gRPC contracts |
| Wafish.Runtime | Runtime startup, plugin lifecycle, state, proxy middleware, and request logging | Core and plugin implementations |
| Wafish.Administration | Administration APIs, authentication, history, telemetry queries, shared translations, and AI/MCP services | Runtime and storage abstractions |
| Wafish.ReactUi | React workspace, navigation, localization, and embedded frontend assets | Administration |
| Wafish.Storage.Sqlite | SQLite repositories and transactional persistence | Storage abstractions |

Existing core models and plugin abstractions provide shared contracts. Do not duplicate these types in the UI.
Package dependencies must not point from runtime to administration or presentation.
Existing namespaces may remain during assembly extraction. Namespace names do not establish assembly dependencies.

## Hosting APIs

- `AddWafishRuntime` registers runtime services without administration controllers or views.
- `UseWafishRuntime` installs runtime middleware.
- `MapWafishProxy` maps the proxy pipeline and optional host customization.
- `AddWafishAdministration` registers administration services and API controllers.
- `MapWafishAdministration` maps administration endpoints and realtime hubs.
- `AddWafishReactUi` registers React presentation over administration services.
- `MapWafishReactUi` maps the workspace and embedded assets.
- `AddWafishAI` enables AI chat and analysis.
- `AddWafishMcp` registers the separate MCP host. Neither method enables the other feature.

The host selects storage and registers it once. Registration endpoints are disabled by default in the runtime entry point.
Hosts use explicit runtime, administration, and React composition. The old Razor wrappers are removed.

## Behavior boundaries

Runtime owns plugin activation, configuration loading, request state, capture, and forwarding.
Administration owns configuration commands, history, queries, authentication, and operator notifications.
Presentation owns view selection, navigation, locale rendering, and browser interaction.
Plugin navigation contributions remain plain metadata usable by different presentation clients.

Persistence uses candidate configuration. Validation and transaction commit precede activation.
Storage failure preserves the active version. Activation failure reports separate persisted and active outcomes.

## Migration requirements

All packages now target .NET 10. Consumers need a .NET 10 SDK and runtime.
The SDK is selected through `global.json`. Containers use the .NET 10 SDK and ASP.NET Core images.
SQLite consumers must use `Microsoft.Data.Sqlite.Core` with `SQLite3MC.PCLRaw.bundle` 2.7.0.
Adding `Microsoft.Data.Sqlite` also adds a competing native bundle and can break encrypted storage.
The storage factory preserves the SQLCipher 4 format when a database password is configured.
Plaintext databases remain plaintext. See [storage migration and backup guidance](security.md#native-sqlite-replacement-sec-14).
Rebuild consumers after these assembly changes. Moved types retain their namespaces.
Custom storage and snapshot compiler implementations require the interface changes documented below.
Do not add remote administration transport in this phase. See the [deferred control-plane proposal](../PLAN.md#idea-01).

## Verification

Build and test after each extraction. Preserve existing controller routes and serialized field names during the move.
Start a host that references runtime and SQLite only. Verify configuration, plugins, and forwarding without dashboard dependencies.
Start the full sample and verify authentication, administration APIs, React pages, and embedded assets.

## Administration hosting

Administration owns controllers, authentication, history, realtime hubs, and shared translations.
`DashboardAuthController` provides POST login, logout, and authentication APIs.
`DashboardSettingsController` provides system information and database downloads.

### Host administration without Razor

Reference `Wafish.Administration` and your storage implementation. The dashboard package is unnecessary.

```csharp
using Wafish.Administration;
using Wafish.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWafishStorage();
builder.Services.AddWafishAdministration();

var app = builder.Build();
app.MapWafishAdministration();
app.Run();
```

Existing `Gateway:Dashboard` configuration controls the route prefix, authentication, localization, and log settings.
For API-key authentication, set `Gateway:Dashboard:Auth:AuthMode` to `ApiKey`.
Supply the key through `Gateway__Dashboard__Auth__ApiKey` or another configuration provider.
Storage configuration remains under `Gateway:Storage`.

This host serves administration APIs and realtime hubs. It has no Razor engine, login page, or dashboard assets.
POST login remains available for configured JWT modes. GET login returns 405 without the presentation package.

To forward proxy traffic in the same host, use `UseWafishGateway()` before mapping administration.
Their namespace is `Wafish.Runtime`. Keep host authentication and authorization middleware in the required order.

### Replace the removed Razor UI

`AddWafishDashboard()` and `UseWafishDashboard()` are removed.
Replace them using the [React hosting example](react-ui.md#host-registration).
Replace `AddWafishDeployment()` with `AddWafishAdministrationDeployment()`.
`UseWafishGateway` owns deployment middleware, compression, plugin activation, and proxy mapping.
Map administration APIs and the React UI explicitly after gateway middleware.
No Razor view engine, page controller, static asset handler, or compatibility UI remains.

### Consumer migration

- Upgrade administration and runtime packages. Replace the dashboard package reference with `Wafish.ReactUi`.
- Existing CLR namespaces and JSON names remain unchanged.
- Public types moved between assemblies. Rebuild consumers and plugins that reference these types.
- Code that depended on assembly-qualified names must use the new owning assembly.
- Remove code that constructs `DashboardPagesController`. React owns the login screen at `/console`.
- System and database actions remain in `DashboardSettingsController`.
- API clients keep their existing URLs, methods, credentials, and payloads. Old Razor page URLs are removed.

AI and MCP require explicit service registration. Configuration alone cannot activate either feature.
The sample explicitly registers AI. MCP remains unregistered until its security checks pass.
Persistence ordering changes remain under ARCH-06 and ARCH-07.

See [ARCH-03 verification](../ARCHIVE.md#archive-tests-wafish-regression-evidence-arch03-readme-md).
The [backlog](../PLAN.md) records remaining work.

## Configuration transactions: ARCH-06 and ARCH-07

Configuration changes now use isolated candidates. Readers receive copies of active configuration.
The gateway validates candidates before storage. SQLite commits routes, clusters, destinations, binding identities, and revision together.
The gateway publishes only after the transaction commits. Failed writes leave active configuration unchanged.

Storage providers must implement `IConfigurationSnapshotRepository`.
`SaveAsync` requires the expected stored revision. Reject stale revisions without changing any rows.
`LoadAsync` must return one consistent revision. Individual repository writes do not replace this transaction contract.
SQLite migration 017 adds the revision record. Existing route and cluster UIDs remain stable.

Operation results retain `Success` and `Message`. They add `Status`, `PersistedVersion`, and `ActiveVersion`.

| Status | HTTP status | Required action |
|---|---|---|
| Applied | 200 | Continue. The candidate was stored and published. |
| ValidationRejected | 400 | Correct the configuration. |
| VersionConflict | 409 | Reload current configuration before another update. |
| StorageFailed | 503 | Correct storage access, then retry. |
| ActivationFailed | 500 | Inspect both revisions. Restart to activate the stored revision. |

The active revision identifies the published configuration. Existing requests can finish against earlier snapshots.
A pending stored revision blocks further configuration writes until restart.
Startup now fails when loading, validating, storing, or publishing configuration fails.
Success audit events follow storage and publication. Batch results report commit failures for all affected items.
`ReplaceAllConfig` throws `ConfigurationUpdateException` with the structured outcome when replacement fails.

Custom snapshot compilers must accept candidate route and cluster identity maps.
These maps preserve plugin bindings when validation occurs before a rename is committed.

```csharp
var result = await configuration.TryAddRouteConfig(route);
if (!result.Success)
{
    logger.LogError("Update failed: {Status}; stored {Stored}; active {Active}",
        result.Status, result.PersistedVersion, result.ActiveVersion);
}
```

The gRPC registration, deregistration, and destination update responses now include `status`, `persisted_version`, and `active_version`.
Regenerate clients from `GatewayRegistry.proto` to consume these additive fields.
Multi-path registration commits the cluster and all routes in one transaction.

### Proxy-only host

```csharp
using Wafish.Runtime;
using Wafish.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWafishStorage();
builder.Services.AddWafishRuntime();
var app = builder.Build();
app.UseWafishGateway();
app.Run();
```

Set `Gateway:Deployment:Mode` to `ProxyOnly` when endpoint-role isolation is required.
Registration stays disabled unless `enableRegistration: true` is supplied explicitly.

## Route activation and rename preservation (ARCH-10)

SQLite migration 018 adds persistent route activation state.
Existing database rows default to enabled because earlier versions did not store this state.
New disabled routes remain disabled after restart.
Route renames preserve native match fields, metadata, transforms, identities, and activation.
Cluster renames preserve destination settings.

Configuration exports and snapshots include an optional `GatewayState.RouteActivation` map beside `ReverseProxy`.
Keys contain route identifiers. Values contain Boolean activation states.
Imports and rollback apply this map within the configuration transaction.
Older documents without this section remain supported.
Snapshot comparisons report activation changes separately from native YARP changes.

Custom `IDynamicYarpConfigService` implementations must accept the new optional `routeActivation` argument.
This applies to `ImportBatchAsync` and `ReplaceAllConfig`.

## Optional React presentation

`Wafish.ReactUi` contains the modern administration workspace.
It references administration services without referencing the Razor package.
Its embedded assets support standalone hosts and container publishing.
See [registration, development, authentication, and migration](react-ui.md).

## Redirect endpoints

The `redirect` plugin compiles immutable templates into route execution plans.
`MapWafishProxy` wraps the matched endpoint before YARP initializes its cluster feature.
This permits redirect-only routes without placeholder clusters or destinations.

Authentication, authorization, and global WAF middleware retain their normal positions.
The redirect branch applies bound logging, route metrics, and local or Redis rate limits.
Completed redirects bypass forwarding policies. HTTPS pass-through uses the normal proxy pipeline once.
A request retains its plugin domain lease through nested middleware and endpoint execution.

Preview builds a private matcher from active endpoint metadata. It does not register endpoint data sources globally.
See the [redirect reference](../src/Wafish.Plugin.Redirect/README.md) for configuration and failure behavior.
