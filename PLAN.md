# Wafish remaining work

Updated: 2026-11-28. Reviewed against the current checkout.

This backlog contains unresolved work only. Each item identifies current behavior, remaining scope, acceptance criteria, and source entry points.
The R36–R55 entries below remain optional proposals. R56 and IDEA-01 remain deferred by user decision.
Source files establish implementation status. Branch names and previous discussions do not establish completion.

Complete each task only after implementation and acceptance checks pass.
Record compatibility impact and verification with the change. Retain task IDs when priorities change.
Publication requires a separate release decision and verification of the exact artifacts.

## Approved gateway feature stack

The [implementation specification](docs/gateway-feature-implementation.md) records behavior, limits, acceptance checks, and PR order.
The [competitor findings](docs/competitor-feature-gaps.md) record the original gaps.

| Item | Status | Remaining work | Source entry point |
| --- | --- | --- | --- |
| G01-EXT | DEFERRED | Dependent calls, partial responses, custom composition, writes, streaming, and aggregate caching. | [Aggregation handler](src/Wafish.Plugin.Aggregation/AggregationHandler.cs) |
| G06-FULL | DEFERRED | Principal cloning, authentication integration, early stages, idempotence, issuer semantics, and snapshot ownership. | [Gateway pipeline](src/Wafish.Runtime/GatewayPipelineExtensions.cs) |

Current: G06 downstream mappings, G13 route authorization, and G01 parallel JSON reads are implemented.
Internal child execution retains configuration and runtime ownership.
Full principal transformation remains deferred under G06-FULL.
Acceptance: Each item meets the specification and scoped checks. Downstream mapping alone does not complete G06-FULL.

<a id="r56"></a>

## R56: Reconcile the release version and package publication

Status: DEFERRED. Publishing automation is implemented; release reconciliation and confirmed publication remain separate work.

Current: Workflows derive versions from release tags or manual inputs. Local builds default to `0.0.0-dev`.
Package verification covers 24 package projects. This source review does not establish their current publication status.

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

## R45: Qualify the initial CI performance baseline

Status: PARTIAL. Comparison tooling, budget policy, CI workflow, and verification are implemented.

Current: Managed schema-5 reports have explicit compatibility, failure, regression, and noise checks.
The performance workflow selects a successful earlier run and retains raw reports and comparison decisions.
Initial budgets cover throughput, P95, and P99 across baseline, plugin, and streaming profiles.
Local unchanged-source verification produced noisy measurements. These reports are not approved CI baselines.

Remaining: Run the performance workflow in a stable environment and qualify all three profiles.
Select its successful run ID as the initial baseline. Review tolerances against repeated measurements from that environment.

Acceptance: All profiles qualify without request failures or excessive noise.
Compatible candidate runs produce actionable budget decisions. Incompatible or noisy runs cannot pass the gate.

Source entry points:

- [Budget comparator](scripts/check-performance-budgets.py) — validation, compatibility, and budget decisions.
- [Budget policy](tests/Wafish.Performance/budgets.json) — minimum repetitions, durations, and tolerances.
- [Performance workflow](.github/workflows/performance-budgets.yml) — representative profiles and selected baseline artifacts.
- [Operator guide](docs/performance-budgets.md) — qualification, comparison, and exit codes.
- [Benchmark session](tests/Wafish.Performance/Runner/BenchmarkSession.cs) — report metadata and configuration identity.

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

## EL01-EXT: Extend external visitor login

Status: DEFERRED. The OIDC session and downstream API implementation is complete.

Current: Startup site profiles select OIDC providers. Route bindings enforce visitor cookies and optional API token forwarding.
Redis shares encrypted sessions and refresh ownership. The optional Aspire profile verifies Keycloak and two gateway replicas.

Remaining decisions:

| Extension | Rethink before implementation | Source entry point |
| --- | --- | --- |
| Direct GitHub and other OAuth adapters | Provider-specific identity verification, stable subject mapping, and API token compatibility. | [Scheme registration](src/Wafish.Plugin.ExternalLogin/ExternalLoginExtensions.cs) |
| Token exchange or gateway-issued API tokens | Issuer ownership, signing keys, audiences, scope reduction, revocation, and trust between services. | [Token refresh and authorization](src/Wafish.Plugin.ExternalLogin/ExternalLoginRuntime.cs) |
| Account linking | Verified ownership, account storage, conflict resolution, and explicit user consent. | [Provider and site profiles](src/Wafish.Plugin.ExternalLogin/ExternalLoginOptions.cs) |
| Provider-wide and backchannel logout | Validated logout messages, subject/session indexing, race handling, and provider differences. | [Session store](src/Wafish.Plugin.ExternalLogin/RedisVisitorSessionStore.cs) |
| Live provider management | Secret storage, atomic scheme replacement, pending callback ownership, and session invalidation. | [Scheme registration](src/Wafish.Plugin.ExternalLogin/ExternalLoginExtensions.cs) |
| Broader provider certification | Real Google and Microsoft tenant tests, consent behavior, resource scopes, and refresh requirements. | [Browser checks](scripts/check-external-login.mjs) |

Acceptance: Each extension defines its identity and token trust boundaries before adding implementation.
Add provider-specific positive and negative tests. Preserve administration isolation and existing route authorization.
Certificate issuance remains infrastructure-owned. Shared parent-domain cookies and browser token export are outside the current contract.
