# Changelog

## [Unreleased]

- Rename projects, packages, namespaces, and extension methods to WafIsh.
- Use `WafIsh.slnx` and central package versions in `Directory.Packages.props`.
- Use English documentation, UI labels, alerts, and AI prompts.
- Remove duplicate Chinese resources and root PowerShell and batch scripts.

## [2.3.0.30] - 2026-09-20

The stable release includes all features from 2.3.0.30-preview.1, plus branding and packaging updates.
These include the plugin architecture, response compression, Redis rate limiting, native YARP configuration editing, and configuration failure alerts.
See the preview entry below for details.

- Fix known bugs in `2.3.0.30-preview.1`.

## [2.3.0.30-preview.1] - 2026-08-28

This prerelease collected feedback and verified stability. At release time, production users were directed to stable version 2.3.0.25.
It includes all changes since 2.3.0.25.
Feedback was collected through GitHub Issues until 2026-09-04, with a stable release planned seven days later.

### New features

- Complete the plugin architecture with 11 first-party plugins and plugin-driven navigation.
  All plugins implement `ConfigureDashboard()` to supply navigation entries.
  `PluginDashboardInitializer` collects menus and widgets at startup.
  `_DashboardLayout.cshtml` renders the plugin group dynamically, including `data-plugin-requires`, localized labels, and server fallback titles.
  Remove hardcoded plugin menu entries.
- Add `WafIsh.Plugin.Compression`, a route-scoped proxy pipeline plugin.
  Enable Gzip/Brotli according to allowed MIME types and minimum response size.
  Support Optimal, Fastest, and NoCompression levels.
  Set Content-Encoding and Vary correctly, and remove Content-Length.
  Add the `/compression` management page and binding controls.
- Add `WafIsh.Plugin.RateLimit.Redis` with atomic Lua scripts for FixedWindow, SlidingWindow, and TokenBucket algorithms.
  Apply accurate global limits across gateway instances.
  Return Retry-After and X-RateLimit-* headers with 429 responses.
  Load StackExchange.Redis optionally through reflection; allow requests through when the host has no Redis reference.
  Add the `/rate-limit-redis` management page and binding controls.
- Confirm six discovery providers in `ServiceDiscoveryRefreshService`.
  Consul filters healthy instances; Nacos uses healthyOnly and parses hosts.
  Eureka parses JSON and `port.$`, filtering for UP status.
  Kubernetes uses ServiceAccount bearer tokens and parses endpoints.
  HttpJson and Static are also supported.
- Add a shared form/JSON dialog for creating and editing clusters and routes.
  JSON Schema drives grouped cards for basic fields, objects, and arrays.
  Support `patternProperties` dynamic objects, including Destinations, and array item editing.
  JSON mode supports formatting, compact output, and validation.
- Detect configuration application failures through YARP `IConfigChangeListener` and publication checks through `IConfigValidator`.
  Store errors in `IProxyConfigErrorStore`, deduplicating reload/prevalidate sources and preferring reload errors.
  Show overview alerts, list banners, and inline cluster/route errors.
  Expose errors through `GET /api/config/apply-errors`.

### Architecture

- Remove all native plugin adapters and use native YARP JSON schemas directly.
  These include `ClusterSchema.json`, `RouteSchema.json`, and `ConfigurationSchema.json`.
  The form/JSON editor removes field overrides and inconsistent behavior from the adapter layer.
  The `allclusterprops` test cluster exposed invalid HealthCheck Policy and Affinity configuration through this change.
- Add 19 translation keys for form/JSON selection, validation panels, and configuration comparison.
  Fix `__('key') || 'fallback'`: missing keys returned their own names, preventing the fallback.
- Record all milestone execution states in `docs/plans/route-cluster-plugin-architecture.md`.

### Fixes

- Parse wildcard `applicationUrl` bindings from `launchSettings.json`, including `http://*:port` and `http://+:port`.
  Previously, `new Uri()` threw `UriFormatException`, leaving unresolved addresses registered with the gateway.
  Wildcards now participate in all-interface detection and LAN IP resolution like `0.0.0.0` and `::1`.
- Match expanded and empty cluster/route row `colspan` values to their table headers.

### gRPC registration: WafIsh.Grpc and WafIsh.Client

- Extend `GatewayRegistrationOptions` with `MatchPaths`, `LoadBalancingPolicy`, `ExtraDestinationAddresses`, and `Metadata`.
  Multiple paths create routes sharing one cluster.
  Support PowerOfTwoChoices, Random, RoundRobin, and LeastRequests policies.
  Include all extra destinations in load balancing and transmit all fields through gRPC registration.
  Previously, only one path and destination were sent; load-balancing policy and metadata were lost.
- Expose `GetServicesAsync` and `UpdateDestinationsAsync` for gRPC service queries and complete destination replacement.
- Register again after three consecutive heartbeat failures, recovering registration after gateway restarts.
  Resume heartbeats after successful registration and log gRPC heartbeat exceptions.
- Remove all `{serviceId}-path{N}` routes during unregistration, with exact service matching and path suffix validation.
- Fall back to `{serviceId}-path{N}` when heartbeats cannot find `{serviceId}`.
- Preserve the cluster LoadBalancingPolicy when updating destinations.
  Previously, complete replacement through `TryAddCluster` silently reset it to null.

### Notes

- Use in-process hot reload through `PluginRuntimeDomainManager`, DI registration, and atomic middleware reconstruction.
  Process-level blue/green replacement is unsuitable for a NuGet library embedded in a host process.
  Host deployment strategies, such as Kubernetes rolling updates, provide zero downtime.
- Add compression/rateLimitRedis translation keys in core.json and plugins.json.
- Recorded Release validation completed with zero warnings and errors, removing eight previous warnings.
  The historical translation check aligned 2,334 keys across 11 files in each supported locale.

## [2.3.0.25] - 2026-07-14

AI operations, WAF and logging changes, core decomposition, SignalR updates, and localization restructuring.

### New features

- Add an embedded chatbot for natural-language gateway management, including routes, clusters, and policies.
  Use OpenAI-compatible APIs, SSE streaming, and function-calling tools.
- Add AI settings for Provider, BaseUrl, API key, model, and Temperature, including Qwen and DeepSeek support.
- Analyze logs and configuration in a background service to identify anomalies.
- Generate AI analysis summaries for notifications.
- Broadcast overview traffic and status through SignalR instead of HTTP polling.
- Split WAF checks into independent, chained rules for IP access, SQL injection, XSS, traversal, size, and security headers.
- Collect request and response logs separately, with sampling, filtering, dynamic settings, SQLite batching, and aggregate queries.
- Adjust log levels, sampling rates, and retention at runtime.
- Introduce `ISchemaMigration` and nine scripts for SQLite schema upgrades.

### Improvements

- Merge ProxyRequest and ProxyResponse entries with the same traceId into one history row.
  Expanded rows show the upstream request, downstream request, and response.
- Estimate history totals from the pairing ratio to reflect merged rows.
- Redesign the overview layout and connect SignalR data.
- Add notification cooldowns, warmup, and channel/rule management.
- Filter routes by cluster ID, including `(none)` for unbound routes.
- Refresh the overview QPS chart every ten seconds.
- Move inline translations into ten JSON files per supported locale.
- Standardize local time with `DateTime.Now` and SQLite `localtime` to correct time offsets.

### Architecture

- Split the 2,000-plus-line `DynamicYarpConfigService` into `ClusterConfigManager`, `RouteConfigManager`, `DynamicConfigPublisher`, `DynamicConfigPersister`, and `YarpConfigParser`.
- Add `ProxyLogMetaEntity`, `ProxyLogBodyEntity`, `SqliteProxyLogAggregator`, and `SqliteProxyLogBatchWriter`.
- Add `DashboardViewLocationExpander` for modular view locations.

### Removed

- Remove WAF security event storage, persistence, AI tools, and the separate event page.
  WAF blocking logs and notifications remain.
- Remove the Benchmarks project.
- Move docker-compose, systemd, and Windows installation scripts to a separate repository.
- Remove GitHub Actions configuration and GatewayTracing options/service extensions.

### Fixes

- Fix an asynchronous race that prevented history from loading on the first tab selection.
- Correct an eight-hour SQLite `strftime` offset using `localtime`.
- Replace `DateTime.UtcNow` with `DateTime.Now` throughout the project for consistent local time.

## [2.3.0.24] - 2026-06-28

### New features

- Support complete YARP configuration in the dashboard.
  Include route Host/Header/Query/CORS/Auth matching and cluster affinity, health, and HTTP settings.
- Show request logs immediately, before responses arrive.
- Configure CORS and authorization middleware automatically.

### Fixes

- Preserve advanced properties after configuration changes and page refreshes.
- Show actual added/deleted content instead of null in comparisons.
- Create a snapshot after configuration import.
- Correct English labels on statistics, history, notification, and deployment pages.

### Cleanup and improvements

- Remove `ConfigurationFileWatcher`, simplify logs to requests/responses, and remove manual deployment-page actions.
- Unify initialization across 16 pages and make the log layout adapt to screen height.

## [2.3.0.22] - 2026-06-26

### New features

- Preserve all native YARP properties when saving routes and clusters through the dashboard.
  Include SessionAffinity, HttpClient, HttpRequest, full Match conditions, and Auth/Cors/Timeout policies.
- Add a global API progress bar, load retry buttons, and offline detection.
- Accept JSON comments (`//` and `/* */`) and trailing commas in editors and imports.
- Support `IsCaseSensitive` for Header and QueryParameter matching.

### Fixes and improvements

- Prevent duplicate endpoint bindings and address conflicts with `Kestrel:Endpoints`.
- Display HttpRequest properties, including ActivityTimeout and Version, in cluster details.
- Accept `Routes`/`routes` and `Clusters`/`clusters` during import.
- Add audit loading indicators and retryable cluster-load errors.
- Add an official-account QR code to the historical README.
- Add the complete configuration reference `docs/yarp_all.json`.

## [2.3.0.21] - 2026-06-24

Flexible ports and deployment modes, health checks, configuration reload, TOTP, UI changes, and performance improvements.

### Ports and deployment modes

- Define multiple IP/port endpoints through `Kestrel:Endpoints`.
- Support Auto, AllInOne, Split, ProxyOnly, and DashboardOnly modes.
- Route requests by LocalPort and role through `EndpointRouterMiddleware`.
- Support Proxy, Dashboard, Admin, Health, and All roles.
- Default `RequireLoopbackForDashboard` to true; reject public dashboard bindings at startup.
- Add `--deployment split --proxy-url ... --dashboard-url ...` arguments.
- Preserve existing `Urls` configuration behavior.

### Health checks

- Add `/health`, `/ready`, and `/live` endpoints.
- Optionally check database connections and YARP configuration loading.
- Support optional IP allowlists and tokens through `X-Health-Token` or `?token=`.
- Return Kubernetes-friendly JSON with status, checks, uptime, version, and endpoints.

### Configuration reload

- Watch `appsettings.json` and `appsettings.{Env}.json` with FileSystemWatcher.
- Poll every 30 seconds to handle missed temporary-file rename events.
- Debounce changes for 500ms and restore the previous snapshot after reload failure.
- Keep five versions in `.config-snapshots/` for manual rollback.
- Alert on Kestrel endpoint changes without restarting automatically.

### TOTP authentication

- Add `TotpHelper` with Base32 secrets and `otpauth://` URIs for authenticator applications.
- Detect two-factor authentication during login; show a code field after HTTP 202 and change the button to Verify.
- Add setup with secret/QR generation, binding verification, disable controls, and live status.
- Persist enabled state in `twofactor-state.json` across restarts.
- Add `login.verify`, `login.verifying`, and `config.twofactor*` translations.

### System health

- Extend `DashboardInfoResponse` with CPU, working set, total memory, GC counts, and thread counts.
- Calculate CPU from processor/elapsed time and collect GC/thread data in `DashboardInfoQueryService`.

### Login and navigation

- Add a translucent login card with `backdrop-filter: blur(20px)`.
- Add an indigo/purple/blue brand panel, glass icons, and decorative circles.
- Animate 24 rising bubbles with six random colors, horizontal motion, and scaling.
- Add a gradient login button with a shadow and hover motion.
- Display `v2.3.0 · MIT License · © 2026 WafIsh` below login.
- Group navigation into Dashboard, Gateway Management, Monitoring, Security, and System Management.
- Expand the current group automatically and collapse other groups.
- Add a three-color brand icon, glow, subtitle, gradient avatar, and pulsing online indicator.
- Add language and logout icon buttons.
- Increase body text from 14px to 15px, menu text from 13px to 14px, and group labels from 10.5px to 13px.

### Performance

- Convert five Inter fonts from TTF to WOFF2, reducing 1,590KB to 553KB (65%).
- Remove `Cache-Control: no-store`; existing version query parameters allow safe caching.
- Change Brotli/Gzip from Fastest to Optimal and add font MIME types.
- Remove eight unused Monaco language packs (1,747KB) and 74 basic-languages directories (450KB).
- Move 17 page modules from Layout to page `@section Scripts`, saving approximately 500–700KB per page.
- Load eight editor scripts only on Settings, Clusters, and Routes pages.
- Remove approximately 97 duplicate script references across 13 pages.

### Fixes

- Return 400 instead of 401 for incorrect TOTP codes, preventing unwanted login redirects.
- Read `resp.secret` instead of `resp.data.secret`; DashboardApi already unwraps responses.
- Remove a stray duplicate block after `resetAll` in dashboard-plugins.js.
- Define DashboardApp in dashboard-core.js with modules, registerModule, and navigateTo.
- Add dashboard-home.js and direct API fallback rendering to Overview.cshtml.
- Replace the nonexistent `DashboardApi.getInfo()` with `DashboardApi.endpoints.getInfo()`.
- Initialize ServiceTabs immediately when DOMContentLoaded has already fired.

### Cleanup

- Remove 48 console.log calls, 277 separator comments, and 59 redundant JavaScript comments (384 total).
- Remove 42 C# separator comments while retaining XML documentation.
- Update the historical NuGet script with two Storage projects, dependency-order packaging, and `-SkipRestore`.

### Added files

```text
src/WafIsh.Dashboard/
├── Infrastructure/
│   ├── Deployment/
│   │   ├── DeploymentOptions.cs        # Options and five modes
│   │   ├── EndpointRoleResolver.cs     # Port-to-role mapping
│   │   ├── DeploymentConfigValidator.cs # Startup validation
│   │   ├── ConfigSnapshot.cs           # Snapshot models and file storage
│   │   └── DeploymentCli.cs            # Command-line parsing
│   ├── Middleware/
│   │   ├── EndpointRouterMiddleware.cs # Port-based routing
│   │   └── HealthCheckMiddleware.cs    # Health checks
│   ├── HostedServices/
│   │   ├── ConfigurationFileWatcher.cs # File reload
│   │   └── KestrelEndpointChangeDetector.cs # Endpoint change detection
│   └── Alert/
│       ├── IGatewayAlertService.cs     # Alert interface
│       └── NullGatewayAlertService.cs  # Default implementation
├── Infrastructure/Auth/
│   └── TotpHelper.cs                   # TOTP authentication
├── Modules/Dashboard/
│   ├── Controllers/DeploymentInfoController.cs # /api/deployment/* endpoints
│   └── Views/Dashboard/Deployment.cshtml         # Deployment status
└── wwwroot/js/modules/
    └── dashboard-deployment.js         # Frontend module

samples/SampleGateway/appsettings.examples/
├── appsettings.AllInOne.json           # Compatibility example
├── appsettings.Split.json              # Separate ports
├── appsettings.SplitWithHealth.json    # Health checks
├── appsettings.ProxyOnly.json          # Proxy only
└── README.md                           # Instructions
```

### Historical usage examples

```csharp
// Enable deployment in Program.cs.
builder.Services.AddWafIshDeployment(builder.Configuration);

// Add middleware before UseWafIshDashboard.
app.UseMiddleware<EndpointRouterMiddleware>();
app.UseMiddleware<HealthCheckMiddleware>();
app.UseWafIshDashboard();
```

```json
{
  "Kestrel": {
    "Endpoints": {
      "Proxy": { "Url": "http://0.0.0.0:80" },
      "Dashboard": { "Url": "http://127.0.0.1:5000" }
    }
  },
  "Gateway": {
    "Deployment": {
      "Mode": "Split",
      "EndpointRoles": { "Proxy": "Proxy", "Dashboard": "Dashboard" }
    }
  }
}
```

The historical design document was `docs/flexible-ports-and-startup-design.md`.

### Deployment status page

- Add System Management → Deployment at `/{prefix}/deployment`.
- Highlight the current Auto, AllInOne, Split, ProxyOnly, or DashboardOnly mode.
- Show process start time, live uptime, version, and environment.
- List endpoint names, addresses, ports, roles, public exposure, and status.
- Show reload settings: enabled state, files, debounce, polling, and rollback.
- Show health-check state, paths, authentication, and checks.
- Highlight public Dashboard/Admin/Health exposure in red.
- List snapshot times, triggers, files, and details.
- Add manual reload, snapshot, and health-check actions.
- Aggregate runtime information through `GET /api/deployment/summary`.
- Use `DeploymentOptions`, `EndpointRoleResolver`, and `IConfigSnapshotStore` as data sources.

## [2.3.0.20] - 2026-06-13

Milestone release: WAF, notifications, health panels, circuit-breaker/rate-limit/retry dashboards, storage decomposition, SQLCipher, and database downloads.
Expand the README with technology, middleware, plugins, policies, and performance documentation.

### WAF

- Add the 424-line `WafMiddleware` before the YARP proxy pipeline.
- Support exact IPs, CIDR ranges, and wildcards, with allowlist precedence and cached regular expressions.
- Use two precompiled SQL detection expression sets with a 5ms ReDoS timeout.
- Detect script tags, `javascript:` URLs, and injected event handlers.
- Detect `../`, URL-encoded traversal, and double encoding.
- Limit body size (10MB default), header count, header size, and URI length (4096 default).
- Inject X-Content-Type-Options, X-Frame-Options, X-XSS-Protection, Referrer-Policy, and Content-Security-Policy.
- Allow per-route control through `Waf:Enabled` metadata.
- Return 403 JSON with `waf:true` for blocked requests.
- Add Waf.cshtml settings for global/rule switches, IP lists, and limits.
- Persist settings through `WafSettingsPersistenceService`, with memory caching, `SemaphoreSlim`, and appsettings.json fallback.
- Add Security.cshtml and dashboard-security.js with 15-second refresh, event filters, attack charts, and top attacker IPs.
- Keep the latest 1,000 events in a `ConcurrentQueue` through `WafEventStore`, with query and clear operations.
- Add `/api/security-events` queries, clear, test events, batch tests, and statistics.

### Notifications

- Support multiple webhook channels with separate timeouts and retries.
- Control rules for circuit openings, exhausted retries, WAF blocks, proxy errors, and rate limits independently.
- Notify on route/cluster creation, deletion, updates, renames, and configuration rollback.
- Suppress repeated alerts during cooldown periods.
- Separate event notifications through `IConfigChangeNotifier`.
- Queue events in `ConfigChangeEventDispatcher`, with a three-second startup delay and 200ms polling.
- Use a `readonly struct` PendingNotification to avoid queue allocation pressure.
- Store history in a memory ring buffer and SQLite, with paging and event-type filters.
- Add channel, rule, timeout, retry, cooldown, and history-limit controls.
- Seed default rules for 11 event types through `StartupWarmupService`.

### Health, circuit breakers, and rate limits

- Add HealthCheck.cshtml with cluster health and destination scores.
- Show unhealthy addresses, status, and failure reasons, with an unhealthy-only filter.
- Add ActiveHealthCheckConfig (interval, timeout, policy, path), PassiveHealthCheckConfig (policy, recovery), and ClusterHealthCheckConfig.
- Expose `/api/health-check/clusters` and `/api/health-check/status`.
- Apply default passive checks to all clusters at startup through `DefaultHealthCheckService`.
- Add CircuitBreaker.cshtml with Closed, Open, and HalfOpen states per cluster/destination.
- Show failure counts, thresholds, recovery countdowns, opening times, and last access.
- Allow resetting all breakers after confirmation.
- Show fixed-window, sliding-window, token-bucket, and concurrency-limit states.
- Display limits, windows, active requests, rejections, and request totals.

### Database settings and encryption

- Download SQLite databases from settings for inspection with external database tools.
- Resolve Data Source from ConnectionString, convert relative paths, and stream through `PhysicalFile` in DashboardController.DownloadDatabase.
- Add `SQLitePCLRaw.bundle_e_sqlcipher` for SQLCipher AES-256 encryption.
- Enable encryption with `Password=xxx`, for example `Data Source=gateway-store.db;Password=MyP@ss`.
- Keep encryption transparent to repositories and download interfaces.

### Client registration and gRPC

- Support HTTP REST and gRPC registration in `GatewayAutoRegistrationClient` using `GatewayRegistryClient`.
- Retry startup registration five times, waiting 2, 4, 8, 16, and 30 seconds.
- Set the gRPC port to HTTP plus one and configure HTTP/1.1 and HTTP/2 (h2c).
- Resolve addresses from Kestrel:EndPoints, Urls, then ASPNETCORE_URLS; detect all-interface listeners through KestrelAutoConfigService.
- Support bearer tokens, Basic authentication, and API keys.
- Infer RouteName from the entry assembly, default MatchPath to `/{**catch-all}`, and infer DestinationAddress through RegistrationOptionsResolver.
- Add `WafIsh.Grpc` and GatewayRegistry.proto with RegisterService, UnregisterService, and Heartbeat contracts.
- Mark gRPC route sources and support idempotent replacement in GatewayRegistryService.

### Storage architecture

- Share one `SqliteConnectionFactory` and connection pool across repositories.
  Use double-checked locking to initialize `SQLitePCL.Batteries_V2.Init()` once.
- Replace IGatewayRepository with eight interfaces: IRouteRepository, IClusterRepository, IPolicyRepository, IConfigHistoryRepository,
  IAuditLogRepository, IWafSettingsRepository, IProxyLogRepository, and INotificationRepository.
- Add eight focused SQLite implementations, each owning its tables.
- Create tables lazily through EnsureInitializedAsync with a lock and `_initialized` flag, avoiding startup blocking.
- Change six consumers to inject only one or two required interfaces.
- Register repositories and the connection factory centrally through `StorageServiceCollectionExtensions.AddWafIshStorage()`.

### Events, caches, and warmup

- Separate IConfigChangeNotifier events from IConfigChangeAuditLog auditing plus events.
  Queue ConfigChangeAuditLog events through ConfigChangeEventDispatcher.
- Use memory caches and `SemaphoreSlim(1,1)` double-checked locking for WAF, dynamic configuration, and notification settings.
- Warm four areas concurrently through `Task.WhenAll`: repository tables, query caches, proxy log storage, and notification seeds.
  Record timings in StartupWarmupService.
- Load notification channels, rules, and settings through NotificationWarmupService.PreloadAsync.
- Isolate warmup failures so startup continues.

### Models and configuration

- Split RouteEntity, ClusterEntity, DestinationEntity, ConfigHistoryEntity, PolicyEntity, AuditLogEntity, and ProxyLogEntity into separate files.
- Add ActiveHealthCheckConfig, PassiveHealthCheckConfig, ClusterHealthCheckConfig, HealthCheckConfig, and CircuitBreakerConfig files.
- Convert domain models and storage entities through ConfigEntityMapper with shared System.Text.Json camelCase settings.
- Group dashboard DTOs/models under Modules/*/Models by feature.
- Standardize Gateway:{Subsystem}, including Gateway:Storage, Gateway:Dashboard, and Gateway:Dashboard:Waf.
- Inject dashboard route prefixes into WAF options through `PostConfigure<WafOptions>`.
- Register key interfaces and concrete types as the same singleton, including IConfigChangeAuditLog and ConfigChangeAuditLog.

### Fixes

- Replace ReaderWriterLockSlim with SemaphoreSlim(1,1) in DynamicYarpConfigService to support asynchronous methods safely.
- Synchronize memory metadata in TrySetRouteDisabled and TrySetClusterDisabled.
- Resolve CS0121 ambiguity between EntityMapper and ConfigEntityMapper through explicit namespaces in ConfigPersistenceService.
- Correct StorageOptions namespace resolution in SqliteConnectionFactory.

### Removed

- Remove IGatewayRepository.cs, which combined nine interfaces and IAsyncDisposable.
- Remove the 970-line SqliteGatewayRepository.cs monolith.
- Remove RedisGatewayRepositoryPlaceholder.cs, whose methods threw NotImplementedException.
- Replace IDataStore.cs and IStructuredDataStore.cs with focused repository interfaces.
- Move IDynamicConfigPersistenceService/DynamicConfigPersistenceService responsibilities to IRouteRepository and IClusterRepository.
- Merge DynamicConfigPreloadService and WebhookSettingsPreloadService into StartupWarmupService.
- Remove SqliteDataStore.cs, RedisDataStore.cs, StructuredSqliteStore.cs, and the unnecessary GatewayRepositoryAdapter.cs.

### Localization

- Add over 120 translation key pairs for WAF settings, rules, IP lists, and save/load states.
- Cover notification channels, rules, advanced settings, event descriptions, and the security event viewer.
- Cover health lists, destinations, errors, status labels, circuit states, confirmations, thresholds, rate-limit policies, windows, and statistics.
- Cover database download buttons, descriptions, progress, completion, and failures.

## [2.3.0.10] - 2026-05-24

- Add audit logs with operation type, target, actor, before/after JSON, and timestamps.
- Add `WafIsh.Client` for one-line automatic microservice registration.
- Improve rollback reliability, log-page interaction, code structure, and documentation; remove compiler warnings.

## [2.3.0.9] - 2026-05-19

- Support HTTPS listeners and HTTPS client registration addresses.

## [2.3.0.8] - 2026-05-19

- Update the README.

## [2.3.2.7] - 2026-05-19

- Add IpBasedLoadBalancingPolicy for developers sharing route paths.
  Route by client IP without frontend changes.

## [2.3.0.6] - 2026-05-19

- Support IWebHostBuilder for traditional ASP.NET Core hosts.

## [2.3.0.5] - 2026-05-19

- Fix automatic client registration listening on `0.0.0.0`.

## [2.3.0.4] - 2026-05-15

- Fix access to dashboard frontend assets.

## [2.3.0.3] - 2026-05-06

- Add configuration import/export with standard YARP format conversion.
- Listen on `0.0.0.0` automatically without manual Kestrel address configuration.
- Improve structure and stability.

## [2.3.0.2] - 2026-04-30

- Add runtime language switching.
- Fix log scrollbars.
- Load frontend assets locally instead of from CDNs for offline use.
- Improve log performance and code structure.

## [2.3.0] - 2026-04-28

- Add live request/response logging through the gateway.
- Add header denylisting, JSON field redaction, and sampling rates.
- Push live logs over WebSockets and improve logging middleware.

## [2.0.0] - 2026-04-28

- Initial release, based on YARP 2.3.0, under the MIT license.
- Add an embedded Razor Class Library dashboard.
- Manage routes and clusters at runtime without restarts.
- Add configuration snapshots and one-click rollback.
- Persist `gateway-dynamic.json` atomically.
- Support None, DefaultJwt, CustomJwt, and ApiKey authentication.
- Configure route prefixes through IApplicationModelConvention.
- Use ReaderWriterLockSlim for concurrent access.
- Include SampleGateway and SampleLocalService.
