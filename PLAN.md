# Wafish remaining work

Updated: 2026-11-27. Reviewed against the current checkout.

This backlog contains unresolved work only. Each item identifies current behavior, remaining scope, acceptance criteria, and source entry points.
The R36–R55 entries below remain optional proposals. R56 and IDEA-01 remain deferred by user decision.
Source files establish implementation status. Branch names and previous discussions do not establish completion.

Complete each task only after implementation and acceptance checks pass.
Record compatibility impact and verification with the change. Retain task IDs when priorities change.
Publication requires a separate release decision and verification of the exact artifacts.

<a id="r56"></a>

## R56: Reconcile the release version and package publication

Status: DEFERRED. Publishing automation is implemented; release reconciliation and confirmed publication remain separate work.

Current: Workflows derive versions from release tags or manual inputs. Local builds default to `0.0.0-dev`.
Package verification covers 20 package projects. This source review does not establish their current publication status.

Remaining: Verify package feeds, existing versions, release tags, source identity, registry configuration, and release assets.
Align metadata and installation examples with the selected release. Preserve published versions and release history.

Acceptance: The exact release artifacts pass isolated consumer and supported-platform checks.
Published packages match the selected source and version. Record the released compatibility baseline after publication.

Source entry points:

- [Publication workflow](.github/workflows/publish-packages.yml) — release and manual triggers.
- [Publication preparation](scripts/prepare-publication.py) — version validation, package selection, and artifact manifests.
- [Package verification](scripts/verify-packages.py) — API checks and isolated consumers.
- [Package metadata](src/Directory.Build.props) — local version defaults and package properties.

<a id="r45"></a>

## R45: Enforce CI performance budgets

Status: TODO.

Current: The runner produces measurements and same-run comparisons against Native YARP.
It does not enforce regression budgets against a selected earlier run.

Remaining: Select representative baselines, define tolerances, and compare compatible workloads in CI.
Include workload, transport, plugin composition, and environment identity when deciding whether results are comparable.

Acceptance: Reports distinguish performance regressions, request failures, incompatible baselines, and measurement noise.

Source entry points:

- [Benchmark runner](tests/Wafish.Performance/Runner/Program.cs) — result summaries and baseline comparisons.
- [Benchmark session](tests/Wafish.Performance/Runner/BenchmarkSession.cs) — run configuration and environment identity.
- [Verification workflow](.github/workflows/verify.yml) — CI integration point.

<a id="r38"></a>

## R38: Combine burst and sustained quotas

Status: TODO.

Current: Redis evaluates one quota per acquisition. Token-bucket `burstBalance` does not enforce two independent quota windows.

Remaining: Evaluate short burst and longer sustained allowances in one atomic decision.
Define Redis Cluster key placement, expiry, rejection headers, and compatibility defaults.

Acceptance: Exceeding either allowance rejects the request. Rejection does not partially consume another allowance.

Source entry points:

- [Redis quota store](src/Wafish.Plugin.RateLimit.Redis/RedisLuaRateLimitStore.cs) — acquisition and Lua scripts.
- [Redis plugin](src/Wafish.Plugin.RateLimit.Redis/RedisRateLimitPlugin.cs) — binding schema.

<a id="r36"></a>

## R36: Share local quotas across named route groups

Status: TODO.

Current: Local limiter keys support route partitions and a global partition, but no explicitly named quota groups.

Remaining: Add a validated group name and shared policy ownership across selected routes.
Define behavior for conflicting policies, route changes, and group removal.

Acceptance: Grouped routes share permits while unrelated routes remain isolated.
Configuration changes cannot reset an active group's quota unexpectedly.

Source entry points:

- [Local rate-limit middleware](src/Wafish.Plugin.RateLimit/Middleware/RateLimitMiddleware.cs) — partition and limiter key construction.
- [Local limiter store](src/Wafish.Runtime/Infrastructure/State/InMemoryRateLimiterStore.cs) — limiter ownership and cleanup.
- [Local plugin](src/Wafish.Plugin.RateLimit/RateLimitPlugin.cs) — binding schema.

<a id="r40"></a>

## R40: Measure compression savings

Status: TODO.

Current: Compression selects an encoding and wraps the response stream. Dedicated compression-savings measurements are absent.

Remaining: Measure input bytes, output bytes, selected encoding, and compression cost using bounded metric dimensions.
Preserve streaming, cancellation, and existing response behavior.

Acceptance: Operators can identify routes where compression adds cost without useful byte savings.

Source entry points:

- [Compression middleware](src/Wafish.Plugin.Compression/CompressionMiddleware.cs) — encoding selection and `CompressionWriteStream`.
- [Plugin metrics](src/Wafish.Plugin.Metrics/PluginMetricsMiddleware.cs) — existing meter and bounded metric storage patterns.

<a id="r41"></a>

## R41: Configure allowed compression algorithms

Status: PARTIAL.

Current: One route-level `compressionLevel` applies to either Brotli or Gzip. Encoding selection considers both algorithms.

Remaining: Configure allowed algorithms and separate levels per algorithm.
Preserve the shared-level setting as the compatibility default.

Acceptance: Disabled algorithms are never selected. Quality values, identity fallback, and Vary handling remain correct.

Source entry points:

- [Compression plugin](src/Wafish.Plugin.Compression/CompressionPlugin.cs) — existing `compressionLevel` schema.
- [Compression middleware](src/Wafish.Plugin.Compression/CompressionMiddleware.cs) — `SelectEncoding` and compressor creation.
- [Compression property checks](tests/Wafish.Regression/ProtocolPropertyChecks.Compression.cs) — negotiation and response checks.

<a id="r50"></a>

## R50: Demonstrate blue-green deployment

Status: TODO.

Current: Route updates and snapshot rollback APIs exist. The sample does not provide a blue-green deployment walkthrough.

Remaining: Demonstrate a health-checked switch between two backend versions using existing configuration APIs.
Include cutover verification and rollback instructions.

Acceptance: Cutover and rollback preserve the original route identity and restore the intended backend version.

Source entry points:

- [Sample topology](Wafish.AppHost/AppHost.cs) — gateway and backend resources.
- [Route configuration controller](src/Wafish.Administration/Modules/GatewayConfig/Controllers/RouteConfigController.cs) — route updates.
- [Snapshot manager](src/Wafish.Administration/Modules/GatewayConfig/Services/ConfigSnapshotManager.cs) — snapshot and rollback behavior.

<a id="r48"></a>

## R48: Benchmark multiple destinations

Status: TODO.

Current: Native YARP accepts one `--backend-url` and configures one destination.

Remaining: Support a bounded destination list and explicit YARP load-balancing policy.
Use matching destinations and transport settings in NativeYarp and EnhancedYarp.

Acceptance: Reports identify destination count and request distribution.
The NativeYarp baseline remains independent of Wafish packages.

Source entry points:

- [Native YARP host](tests/Wafish.Performance/NativeYarp/Program.cs) — destination configuration.
- [Enhanced YARP host](tests/Wafish.Performance/EnhancedYarp/Program.cs) — matching Wafish configuration.
- [Benchmark session](tests/Wafish.Performance/Runner/BenchmarkSession.cs) — managed host arguments and result identity.

<a id="r46"></a>

## R46: Add WebAuthn authentication

Status: TODO.

Current: Administration supports configured credentials, sessions, and TOTP. It has no WebAuthn enrollment or challenge flow.

Remaining: Add registered authenticators as an administration login method.
Define credential storage, challenge expiry, revocation, and recovery behavior.

Acceptance: Enrollment, login, expired challenges, revocation, and recovery have integration tests.
Existing authentication modes and session revocation remain supported.

Source entry points:

- [Authentication controller](src/Wafish.Administration/Modules/Dashboard/Controllers/DashboardAuthController.cs) — login and second-factor endpoints.
- [Session store](src/Wafish.Administration/Infrastructure/Auth/DashboardSessionStore.cs) — session lifecycle.
- [React authentication](src/Wafish.ReactUi/ClientApp/src/components/auth.tsx) — sign-in flow.

<a id="r37"></a>

## R37: Partition quotas by trusted tenant

Status: TODO.

Current: Redis quota keys combine the configured prefix, route ID, and client IP.

Remaining: Allow an explicitly configured, authenticated tenant claim to select the shared quota key.
Define claim trust, missing-claim behavior, and bounded key cardinality.

Acceptance: Untrusted values cannot select another tenant's quota or create unlimited quota keys.
Missing claims follow a documented policy.

Source entry points:

- [Redis middleware](src/Wafish.Plugin.RateLimit.Redis/Middleware/RedisRateLimitMiddleware.cs) — request identity and key construction.
- [Redis plugin](src/Wafish.Plugin.RateLimit.Redis/RedisRateLimitPlugin.cs) — partition configuration schema.

<a id="r39"></a>

## R39: Assign weighted request costs

Status: TODO.

Current: The local limiter store acquires one permit per request.

Remaining: Let trusted route configuration assign a validated cost for token and window algorithms.
Define supported algorithms and rejection behavior when the cost exceeds available capacity.

Acceptance: Each route consumes its configured allowance. Invalid costs fail validation, and rejection reports usable retry information.

Source entry points:

- [Local limiter store](src/Wafish.Runtime/Infrastructure/State/InMemoryRateLimiterStore.cs) — `AcquireAsync(1, cancellationToken)`.
- [Local rate-limit middleware](src/Wafish.Plugin.RateLimit/Middleware/RateLimitMiddleware.cs) — acquisition and rejection handling.
- [Local plugin](src/Wafish.Plugin.RateLimit/RateLimitPlugin.cs) — request-cost schema integration point.

<a id="r49"></a>

## R49: Add optional network fault injection

Status: TODO.

Current: Benchmark fixtures already support controlled stalls and disconnects. AppHost has no optional network fault resource.

Remaining: Add a local resource that delays or interrupts backend traffic on request.
Reuse existing fixture behavior where suitable and provide repeatable recovery steps.

Acceptance: The optional scenario demonstrates retries and circuit recovery without changing production defaults.

Source entry points:

- [AppHost topology](Wafish.AppHost/AppHost.cs) — optional resource registration.
- [Benchmark backend](tests/Wafish.Performance/Backend/Program.cs) — existing controlled transport faults.
- [Network checks](tests/Wafish.Regression/BenchmarkReliabilityChecks.Network.cs) — fault and cancellation checks.

<a id="r51"></a>

## R51: Demonstrate service discovery

Status: TODO.

Current: AppHost backends register through gRPC. An optional HTTP JSON discovery catalog is absent.

Remaining: Add a discovery profile with real plugin bindings for the sample backends.
Exercise destination addition, removal, stale discovery, and recovery.

Acceptance: The default topology remains unchanged.
The discovery profile preserves stable destination identities and exposes refresh failures.

Source entry points:

- [AppHost topology](Wafish.AppHost/AppHost.cs) — registration settings and backend resources.
- [Discovery refresh service](src/Wafish.Plugin.ServiceDiscovery/ServiceDiscoveryRefreshService.cs) — refresh and destination publication.
- [Discovery plugin](src/Wafish.Plugin.ServiceDiscovery/HttpServiceDiscoveryPlugin.cs) — binding schema.

<a id="r55"></a>

## R55: Save operator views

Status: TODO.

Current: Table and log filters use component state. Named views have no authenticated persistence API.

Remaining: Persist named filters and column choices through a per-user API.
Define ownership, validation, and restoration after sign-in.

Acceptance: Views restore without storing credentials or sharing private preferences between users.

Source entry points:

- [Workspace components](src/Wafish.ReactUi/ClientApp/src/components/workspace.tsx) — table columns, filters, and pagination.
- [Observation pages](src/Wafish.ReactUi/ClientApp/src/pages/observe.tsx) — log and audit filters.
- [Dashboard authorization](src/Wafish.Administration/Infrastructure/Auth/DashboardAuthorizationService.cs) — authenticated operation permissions.

<a id="r52"></a>

## R52: Demonstrate cancellable streaming

Status: TODO.

Current: The sample controller returns complete responses. The benchmark backend already provides bounded streaming workloads.

Remaining: Add a synthetic sample endpoint with bounded chunk count and interval.
Observe request cancellation and demonstrate cache, compression, and retry behavior.

Acceptance: The first chunk arrives before completion. Client disconnection stops backend work.
The walkthrough identifies which policies preserve streaming or prevent replay.

Source entry points:

- [Sample controller](samples/SampleLocalService/Controllers/LocalServiceController.cs) — sample endpoint integration point.
- [Streaming workload](tests/Wafish.Performance/Shared/BenchmarkStreamWorkload.cs) — bounded workload parameters.
- [Streaming checks](tests/Wafish.Regression/BenchmarkReliabilityChecks.Streaming.cs) — early delivery, cancellation, and buffering checks.

<a id="r53"></a>

## R53: Show redacted telemetry configuration

Status: TODO.

Current: Service defaults configure instrumentation and optional OTLP export. AppHost applies telemetry redaction settings.
A local diagnostic view of effective telemetry configuration is absent.

Remaining: Add an opt-in local view of enabled exporters and instrumentation.
Show configuration state without exposing exporter credentials, sensitive URLs, or payload data.

Acceptance: Operators can inspect effective telemetry settings while secrets remain redacted.

Source entry points:

- [Service defaults](samples/SampleServiceDefaults/Extensions.cs) — `ConfigureOpenTelemetry` and exporter selection.
- [AppHost topology](Wafish.AppHost/AppHost.cs) — telemetry environment and redaction settings.

<a id="r54"></a>

## R54: Demonstrate idempotent orders

Status: TODO.

Current: The sample exposes ping and echo operations. It has no idempotent write example.

Remaining: Add a synthetic order operation using an idempotency key and bounded retained state.
Define key scope, request comparison, expiry, and concurrent duplicate behavior.
This demonstrates application behavior; it does not add gateway-wide idempotency guarantees.

Acceptance: Repeated keys do not create duplicate orders. Conflicting payloads are rejected.

Source entry points:

- [Sample controller](samples/SampleLocalService/Controllers/LocalServiceController.cs) — write endpoint integration point.
- [Sample startup](samples/SampleLocalService/Program.cs) — sample state registration.

<a id="idea-01"></a>

## IDEA-01: Separate administration and gateway processes

Status: DEFERRED. Promote when independent deployment or multi-gateway management is required.

Current: Runtime, administration, and UI have separate packages. Administration still registers the local runtime.
`Split` separates ports within one process; it does not provide a remote gateway-management protocol.

Remaining: Define gateway identity, enrollment, credential rotation, revocation, transport authentication, and operation authorization.
Define configuration ownership, versioning, delivery, acknowledgment, conflicts, and persisted versus active revisions.
Specify disconnection behavior, partial rollout, telemetry attribution, storage ownership, migration, and protocol compatibility.

Acceptance: Enrolled gateways acknowledge authorized updates. Revoked gateways cannot access management operations.
Disconnected gateways follow an explicit last-known-configuration policy. Partial rollout is visible and recoverable.
Updates identify the actor, target gateway, persisted revision, and active revision.
Administration restart does not interrupt gateway traffic.

Source entry points:

- [Administration registration](src/Wafish.Administration/AdministrationServiceCollectionExtensions.cs) — current local runtime dependency.
- [Runtime registration](src/Wafish.Runtime/GatewayRuntimeExtensions.cs) — runtime services and proxy endpoints.
- [Deployment options](src/Wafish.Runtime/Infrastructure/Deployment/DeploymentOptions.cs) — current process and endpoint roles.
- [Configuration publication](src/Wafish/Services/DynamicConfigPublisher.cs) — local snapshot preparation and activation.
