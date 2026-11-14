# Wafish improvement plan

Created: 2026-11-13. Last updated: 2026-11-14.

This file tracks architectural improvements, security fixes, Aspire integration, and UI work.
Complete urgent security fixes before architectural changes.
Keep each phase runnable and independently testable.

The [2026-11-14 .NET source review](DOTNET_REVIEW.md) records new proposals, shared priorities, and all 30 project plans.

## Decisions

- Breaking changes are permitted. Document consumer migrations before completing the affected task.
- Separate packages first. Runtime and administration remain in the same host.
- Make the UI optional and replaceable.
- Remove the old Razor interface after React verification, as requested by the user under UI-08.
- Reject configuration updates when persistence fails. Keep the active configuration and version unchanged.
- Persist validated configuration before activation. Report storage and activation failures separately.
- Keep the existing Razor UI working through the new administration boundary.
- Selected React, TypeScript, Tailwind, and shadcn/ui after the separate design milestone. Keep Razor as an optional fallback.
- Add Aspire integration to sample and test hosts. Keep reusable gateway libraries independent of Aspire.
- Target .NET 10 only, as requested on 2026-11-13. This replaces the earlier framework-preservation decision.
- Keep SQLite as local gateway storage. Add Redis only for scenarios that require it.
- Defer separate administration and gateway processes to [IDEAS.md](IDEAS.md#idea-01-separate-administration-and-gateway-processes).

## Completed priorities 1–13

The naming correction is complete; see [consumer migration](docs/naming-migration.md).

| Priority | Work | Status |
|---|---|---|
| 1 | Redis construction recovery, outage tests, and deployment isolation | DONE |
| 2 | Discovery provider fixtures, binding health, backoff, and stale policies | DONE |
| 3 | Async configuration, cancellation, concurrent mutations, and retry deadlines | DONE |
| 4 | HTTPS fixtures, cleanup, authorization matrix, and session lifecycle | DONE |
| 5 | SQLite recovery, bounded queries, restore verification, storage contracts, and pagination | DONE |
| 6 | Runtime replacement/unload stress, circuit probes, local limiter churn, and shutdown faults | DONE |
| 7 | Shared mutation errors, client outcomes, gRPC mappings, and plugin contracts | DONE |
| 8 | Regression organization, fault schedules, browser fixtures, and UI accessibility | DONE |
| 9 | WAF fuzzing, cache headers, compression negotiation, and metric concurrency | DONE |
| 10 | Benchmark cancellation, preflight, addresses, memory, metadata, and manifests | DONE |
| 11 | Measured cache, compression, and snapshot-write optimization | DONE |
| 12 | AppHost restart/Sonar/isolation and sample deployment checks | DONE |
| 13 | Sample identities, contracts, telemetry, documentation, and probe filtering | DONE |

## Remaining work: ranked release scope

Public-release implementation R01–R14 is active. Stop after its acceptance checks and release verification.
R01 is complete: resource-limit, async-policy, lifetime, and allocation evidence reconciled.
The previous grouped priorities 14–24 are superseded by R01–R55 below.
Historical completion numbers and project task identifiers remain unchanged.

### Recommended finish line

Confirmed target: a public package for external adopters.
Support independent package consumers, existing authentication, SQLite, and the documented plugin extension model.
Public availability does not require multi-tenancy, multiple registration gateways, Kubernetes, or every optional authentication method.
The default administration model remains trusted administrators until operation permissions are implemented.
Document that boundary explicitly; do not claim multi-role isolation.

**Recommended release scope: R01–R14. Stop after these acceptance checks and the public-package release verification pass.**
This means thirteen implementation tasks and one review-status reconciliation, not fifty-five required features.
R15–R35 are follow-up work. R36–R55 are optional extensions and demonstrations.
Their TODO statuses remain unchanged; postponement does not mean completion.
Implementation order within optional work follows actual dependencies, not the need score alone.

This replaces the earlier internal-deployment recommendation. R numbers reflect the revised execution order.
Project task identifiers and historical completed priorities 1–13 remain unchanged.

Ratings express first-release necessity, not implementation effort or a new finding that an existing feature is defective.
A score of 10 means a confirmed release blocker; this planning review assigns none.
Scores 8–9 belong in the recommended release scope. Scores 6–7 are valuable follow-up work.
Scores 4–5 require a concrete deployment need. Scores 1–3 are conveniences or demonstrations.
No delivery dates are estimated from task counts; task sizes differ substantially.

### Inventory and scope

- All 52 TODO tasks from the 30 project plans appear exactly once below.
- R01 reconciles the unresolved NET-IMP-03 status in DOTNET_REVIEW.md against completed implementation evidence.
- R28 and R32 include two distinct solution proposals outside the project PLAN.md files.
- R19 includes the solution-wide NET-FEAT-02 lease proposal; it is not counted twice.
- Total: 54 implementation proposals and one review check, represented by 55 ranked entries.
- Separate administration processes remain a deferred idea in [IDEAS.md](IDEAS.md), outside this queue.

The source plans retain their acceptance criteria. This queue supersedes their older relative P2/P3 ordering.

### Recommended public release: R01–R14

| Rank | Need / 10 | Task | Source | Reason and dependencies |
|---|---|---|---|---|
| R01 | 9 | Reconcile resource-limit and cancellation evidence | [Solution review NET-IMP-03](DOTNET_REVIEW.md#net-imp-03-define-shared-resource-and-cancellation-limits) | Close the unresolved review status against existing evidence; identify any concrete acceptance failure. |
| R02 | 9 | Redact sensitive log fields | [Wafish.Plugin.ProxyLog FEAT-01](src/Wafish.Plugin.ProxyLog/PLAN.md#feat-01-add-per-route-structured-redaction-rules) | Prevent configured sensitive fields from reaching storage or realtime subscribers. |
| R03 | 9 | Bound log storage with quotas | [Wafish.Plugin.ProxyLog FEAT-02](src/Wafish.Plugin.ProxyLog/PLAN.md#feat-02-add-log-storage-quotas) | Keep sustained traffic within a documented disk budget. |
| R04 | 8 | Provide a minimal gateway profile | [SampleGateway FEAT-02](samples/SampleGateway/PLAN.md#feat-02-provide-a-minimal-runtime-composition-profile) | Provide an independently runnable starting point with only runtime and storage; make optional components explicit. |
| R05 | 8 | Validate plugin ordering constraints | [Wafish.Plugin.Abstractions FEAT-02](src/Wafish.Plugin.Abstractions/PLAN.md#feat-02-declare-pipeline-ordering-constraints) | Make extension ordering rules explicit before external plugins depend on the public contract. |
| R06 | 8 | Declare plugin capability requirements | [Wafish.Plugin.Abstractions FEAT-01](src/Wafish.Plugin.Abstractions/PLAN.md#feat-01-declare-host-capability-requirements) | Reject unsupported external plugins before creating their resources; document capability versions. |
| R07 | 8 | Negotiate registration capabilities | [Wafish.Grpc FEAT-01](src/Wafish.Grpc/PLAN.md#feat-01-add-a-capability-handshake) | Let separately upgraded clients discover registration support while retaining older-client behavior. |
| R08 | 8 | Expose UI asset version metadata | [Wafish.ReactUi FEAT-02](src/Wafish.ReactUi/PLAN.md#feat-02-expose-embedded-asset-version-metadata) | Expose reproducible asset identity and administration compatibility to diagnose upgrades outside this repository. |
| R09 | 8 | Spread client startup retries | [Wafish.Client IMP-01](src/Wafish.Client/PLAN.md#imp-01-add-startup-retry-jitter) | Avoid synchronized registration retries during service restarts. |
| R10 | 8 | Limit retries across each cluster | [Wafish.Plugin.Retry FEAT-01](src/Wafish.Plugin.Retry/PLAN.md#feat-01-add-a-cluster-wide-retry-budget) | Prevent extra attempts from amplifying an upstream outage. |
| R11 | 8 | Define metric units and reset behavior | [Wafish.Plugin.Metrics IMP-02](src/Wafish.Plugin.Metrics/PLAN.md#imp-02-define-metric-units-and-reset-semantics) | Establish meaningful measurements before extending their export. |
| R12 | 8 | Describe plugin statistic units | [Wafish.Plugin.Abstractions IMP-03](src/Wafish.Plugin.Abstractions/PLAN.md#imp-03-define-statistic-names-and-units) | Define names, units, and reset descriptors before external consumers depend on resource statistics. |
| R13 | 8 | Export plugin metrics through OpenTelemetry | [Wafish.Plugin.Metrics FEAT-01](src/Wafish.Plugin.Metrics/PLAN.md#feat-01-export-plugin-measurements-through-opentelemetry) | Make existing request and cluster measurements usable by standard collectors. After R11. |
| R14 | 8 | Reproduce stalled and truncated responses | [Backend FEAT-02](tests/Wafish.Performance/Backend/PLAN.md#feat-02-add-deterministic-transport-faults) | Verify transport failure handling with deterministic faults. |

### Follow-up work: R15–R35

| Rank | Need / 10 | Task | Source | Reason and dependencies |
|---|---|---|---|---|
| R15 | 7 | Add operation permissions | [Wafish.Administration FEAT-02](src/Wafish.Administration/PLAN.md#feat-02-add-operation-permissions) | Useful for shared operator teams. Public package availability alone does not require a multi-role administration product. |
| R16 | 7 | Add mutation idempotency keys | [Wafish FEAT-01](src/Wafish/PLAN.md#feat-01-add-mutation-idempotency-keys) | Allow safe retries after uncertain configuration responses. |
| R17 | 7 | Preview configuration changes | [Wafish FEAT-02](src/Wafish/PLAN.md#feat-02-preview-a-configuration-change) | Useful operational protection; existing validation and atomic apply remain the baseline for the first public package. |
| R18 | 7 | Preview plugin activation | [Wafish.Runtime FEAT-01](src/Wafish.Runtime/PLAN.md#feat-01-expose-plugin-activation-previews) | Inspect candidate health and resource needs before publication. Coordinate with R17, R05, and R06. |
| R19 | 7 | Add registration leases and draining | [Wafish.Grpc FEAT-02](src/Wafish.Grpc/PLAN.md#feat-02-negotiate-registration-leases) | Define expiry and stale-renewal protection across protocol, client, core, and storage. After R07; includes NET-FEAT-02. |
| R20 | 7 | Expose registration readiness | [SampleServiceDefaults FEAT-02](samples/SampleServiceDefaults/PLAN.md#feat-02-add-optional-registration-readiness) | Distinguish a live backend from one registered for routing; keep liveness independent. |
| R21 | 7 | Add bounded latency histograms | [Wafish.Plugin.Metrics FEAT-02](src/Wafish.Plugin.Metrics/PLAN.md#feat-02-add-route-latency-distributions) | Useful tail-latency visibility; defer the additional histogram feature until after the base metric contract and export. |
| R22 | 7 | Purge cached responses by route or tag | [Wafish.Plugin.Cache FEAT-01](src/Wafish.Plugin.Cache/PLAN.md#feat-01-add-route-scoped-cache-tags-and-purge) | Give operators controlled invalidation. Apply R15 permissions when available. |
| R23 | 7 | Evaluate WAF rules without blocking | [Wafish.Plugin.Waf FEAT-01](src/Wafish.Plugin.Waf/PLAN.md#feat-01-add-an-explicit-detection-only-mode) | Support staged rule evaluation while blocking remains the default. |
| R24 | 7 | Add narrow WAF exclusions | [Wafish.Plugin.Waf FEAT-02](src/Wafish.Plugin.Waf/PLAN.md#feat-02-add-narrow-rule-exclusions) | Handle legitimate fields without disabling unrelated protection. Include exclusions in R17 previews. |
| R25 | 7 | Expose plugin drain progress | [Wafish.Runtime FEAT-02](src/Wafish.Runtime/PLAN.md#feat-02-report-plugin-drain-progress) | Show held requests, retirement age, and disposal failures during transitions. |
| R26 | 7 | Describe storage provider capabilities | [Wafish.Storage.Abstractions FEAT-01](src/Wafish.Storage.Abstractions/PLAN.md#feat-01-expose-provider-capability-metadata) | Useful for third-party storage providers. The first public release supports the existing SQLite provider explicitly. |
| R27 | 7 | Abstract backup operations | [Wafish.Storage.Abstractions FEAT-02](src/Wafish.Storage.Abstractions/PLAN.md#feat-02-add-a-storage-backup-capability) | After R26. Current SQLite backup already exists; a provider-independent API can follow when another provider is supported. |
| R28 | 7 | Export a redacted diagnostic bundle | [Solution review NET-FEAT-03](DOTNET_REVIEW.md#net-feat-03-add-a-redacted-diagnostic-bundle) | Collect bounded support information. Use R25 drain diagnostics and R26 storage capabilities. |
| R29 | 6 | Split administration tests into modules | [Wafish.Administration.Regression IMP-01](tests/Wafish.Administration.Regression/PLAN.md#imp-01-split-checks-into-focused-modules) | Improve maintenance without changing scenario names or coverage. |
| R30 | 6 | Share concurrent cache fills | [Wafish.Plugin.Cache FEAT-02](src/Wafish.Plugin.Cache/PLAN.md#feat-02-share-concurrent-cache-fills) | Reduce duplicate upstream work with bounded waits and independent cancellation. |
| R31 | 6 | Spread circuit recovery probes | [Wafish.Plugin.CircuitBreaker FEAT-01](src/Wafish.Plugin.CircuitBreaker/PLAN.md#feat-01-add-configurable-recovery-jitter) | Reduce synchronized recovery across replicas. |
| R32 | 6 | Apply versioned configuration change sets | [Solution review NET-FEAT-01](DOTNET_REVIEW.md#net-feat-01-add-versioned-configuration-change-sets) | Coordinate route, cluster, and plugin edits in one operation. After R17 and existing atomic binding storage. |
| R33 | 6 | Support ordered gateway failover | [Wafish.Client FEAT-01](src/Wafish.Client/PLAN.md#feat-01-support-ordered-gateway-failover) | Add multiple registration endpoints with clear ownership. Settle R19 lease semantics first. |
| R34 | 6 | Add destination maintenance windows | [Wafish.Plugin.CircuitBreaker FEAT-02](src/Wafish.Plugin.CircuitBreaker/PLAN.md#feat-02-add-destination-maintenance-windows) | Temporarily remove one destination with an actor, reason, and expiry. Coordinate with R15 permissions. |
| R35 | 6 | Support Kubernetes EndpointSlice | [Wafish.Plugin.ServiceDiscovery FEAT-01](src/Wafish.Plugin.ServiceDiscovery/PLAN.md#feat-01-support-kubernetes-endpointslice-discovery) | Improve Kubernetes discovery when that deployment is required. |

### Optional extensions: R36–R55

| Rank | Need / 10 | Task | Source | Reason and dependencies |
|---|---|---|---|---|
| R36 | 5 | Share local quotas across named route groups | [Wafish.Plugin.RateLimit FEAT-01](src/Wafish.Plugin.RateLimit/PLAN.md#feat-01-support-named-quota-groups) | Useful when several routes represent one shared allowance. |
| R37 | 5 | Partition quotas by trusted tenant | [Wafish.Plugin.RateLimit.Redis FEAT-01](src/Wafish.Plugin.RateLimit.Redis/PLAN.md#feat-01-add-trusted-tenant-quota-partitions) | Needed for tenant-based limits; avoid trusting arbitrary caller headers. |
| R38 | 5 | Combine burst and sustained quotas | [Wafish.Plugin.RateLimit.Redis FEAT-02](src/Wafish.Plugin.RateLimit.Redis/PLAN.md#feat-02-combine-burst-and-sustained-quotas) | Enforce two allowances in one atomic Redis decision. |
| R39 | 5 | Assign weighted request costs | [Wafish.Plugin.RateLimit FEAT-02](src/Wafish.Plugin.RateLimit/PLAN.md#feat-02-support-weighted-request-costs) | Charge expensive routes more than cheap requests. |
| R40 | 5 | Measure compression savings | [Wafish.Plugin.Compression FEAT-01](src/Wafish.Plugin.Compression/PLAN.md#feat-01-expose-compression-savings-metrics) | Show whether byte savings justify compression cost. Coordinate with R13 and R12. |
| R41 | 5 | Configure compression algorithms and levels | [Wafish.Plugin.Compression FEAT-02](src/Wafish.Plugin.Compression/PLAN.md#feat-02-configure-allowed-compression-algorithms) | Allow route-specific CPU and bandwidth tradeoffs. |
| R42 | 5 | Add paced streaming benchmarks | [Backend FEAT-01](tests/Wafish.Performance/Backend/PLAN.md#feat-01-add-paced-streaming-scenarios) | Measure early delivery and buffering under controlled streaming. |
| R43 | 5 | Add matched HTTP/2 and TLS baselines | [NativeYarp FEAT-01](tests/Wafish.Performance/NativeYarp/PLAN.md#feat-01-add-selectable-http2-and-tls-baselines) | Support fair transport comparisons when performance claims require them. |
| R44 | 5 | Schedule request arrival rates | [Runner FEAT-02](tests/Wafish.Performance/Runner/PLAN.md#feat-02-add-scheduled-request-rate-mode) | Reveal overload without the client silently reducing offered load. |
| R45 | 5 | Enforce CI performance budgets | [Runner FEAT-01](tests/Wafish.Performance/Runner/PLAN.md#feat-01-add-ci-performance-regression-budgets) | Detect regressions after representative workloads and stable baselines exist; use R42–R44 as needed. |
| R46 | 5 | Add WebAuthn authentication | [Wafish.Administration FEAT-01](src/Wafish.Administration/PLAN.md#feat-01-add-webauthn-authentication) | An additional login method; document existing authentication and TOTP support without promising passkeys. |
| R47 | 4 | Benchmark selected plugin combinations | [EnhancedYarp FEAT-01](tests/Wafish.Performance/EnhancedYarp/PLAN.md#feat-01-add-a-bounded-plugin-combination-matrix) | Attribute costs to bounded, recorded combinations. |
| R48 | 4 | Benchmark multiple destinations | [NativeYarp FEAT-02](tests/Wafish.Performance/NativeYarp/PLAN.md#feat-02-add-a-multiple-destination-baseline) | Compare load-balancing behavior against a matched native baseline. |
| R49 | 4 | Add optional network fault injection | [Wafish.AppHost FEAT-01](Wafish.AppHost/PLAN.md#feat-01-add-optional-network-fault-injection) | Make local resilience demonstrations easier; deterministic transport tests are already ranked R14. |
| R50 | 4 | Demonstrate blue-green deployment | [SampleGateway FEAT-01](samples/SampleGateway/PLAN.md#feat-01-add-a-blue-green-deployment-example) | Document health-checked cutover and rollback using existing configuration APIs. |
| R51 | 3 | Demonstrate service discovery | [Wafish.AppHost FEAT-02](Wafish.AppHost/PLAN.md#feat-02-add-a-service-discovery-demonstration-profile) | Add an optional discovery profile without changing the default topology. |
| R52 | 3 | Demonstrate cancellable streaming | [SampleLocalService FEAT-02](samples/SampleLocalService/PLAN.md#feat-02-demonstrate-cancellable-streaming) | Provide a synthetic example of streaming and disconnect cancellation. |
| R53 | 3 | Show redacted telemetry configuration | [SampleServiceDefaults FEAT-01](samples/SampleServiceDefaults/PLAN.md#feat-01-add-redacted-telemetry-diagnostics) | Add an optional local diagnostic view of exporters and instrumentation. |
| R54 | 3 | Demonstrate idempotent orders | [SampleLocalService FEAT-01](samples/SampleLocalService/PLAN.md#feat-01-add-an-idempotent-order-example) | Teach safe application writes; separate from gateway configuration idempotency. |
| R55 | 3 | Save operator views | [Wafish.ReactUi FEAT-01](src/Wafish.ReactUi/PLAN.md#feat-01-add-saved-operator-views) | Persist per-user filters and columns; a convenience feature. |

### Public-package release completion rule

1. Reconcile R01 against existing evidence; do not reopen already satisfied acceptance checks.
2. Complete R02–R14 against their source acceptance criteria, with tests and migration notes where needed.
3. Pass the Release build, relevant regression suites, browser checks, and isolated consumers of the actual packed packages.
4. Verify clean installation from a test feed without repository project references or development-only files.
5. Verify public API and protocol compatibility, native SQLite/encryption, and embedded UI assets on every advertised platform.
6. Publish accurate quick-start, configuration, authentication, extension, upgrade, backup/restore, and support-boundary documentation with the release.
7. Define package versioning and breaking-change policy before external adopters depend on these contracts.
8. Record known limitations and release evidence. Stop; optional tasks do not automatically become release requirements.

These are release checks, not additional feature proposals or permission to publish packages now.
A concrete failed acceptance check can block release. Report it explicitly instead of silently expanding this scope.
This planning cutoff does not claim public release verification is already complete.

### Conditions that change the ratings

- Shared operator roles: promote R15 operation permissions to 9 and place it in the release scope.
- Multiple registration gateways: promote R19 and R33; settle lease ownership before failover.
- Tenant-based quotas: promote R37 to 9 and validate the tenant trust boundary before release.
- Kubernetes deployment: promote R35 to 9 when EndpointSlice discovery is required.
- Additional supported storage providers: promote R26 and R27 to 8 before promising provider interchangeability.
- Mandatory passkeys: promote R46 to 9; otherwise existing authentication keeps it optional.

## Tracking rules

Use these statuses consistently:

| Status | Meaning |
|---|---|
| TODO | Work has not started. Listed dependencies must finish first. |
| IN PROGRESS | Work is active. Record the owner and current step. |
| BLOCKED | A decision or external condition prevents progress. Record the reason. |
| DONE | Acceptance checks passed. Evidence and completion date are recorded. |

Task identifiers remain stable when tasks move or expand.
Each task below records its work, affected components, dependencies, and acceptance checks.
For every unfinished task, completion date and evidence are currently absent.
Unfinished implementation is a remaining limitation, not a completed capability.

When starting a task, add its owner and next step to the execution record.
When stopping work, record the next action, failed checks, and blockers.
When completing a task, record the commit, commands, results, and completion date.
Record material limitations beside the evidence. Do not mark a failed acceptance check as complete.
Update phase status only when every required task and exit check passes.

Add completed changes to the changelog at the bottom of [README.md](README.md#changelog).
Use the actual completion date. Put newer entries first.
Keep pending work here. Preserve historical releases in [CHANGELOG.md](CHANGELOG.md).

## Baseline and evidence

### Historical review

The previous review ran on 2026-11-11. Its results are historical, not fresh verification.

- Security grade: 3/10 for production readiness.
- Architecture grade: 6/10.
- The solution built with zero warnings and errors.
- The .NET 8 regression run reported 22 passes and three failures.
- Failures concerned plugin activation, request draining, and plugin-state persistence.
- Local probes reported private-response leakage, empty-key authorization, and buffering beyond the configured cache limit.
- A dependency audit reported a SQLite native-library advisory. Application exploitability was not established.

PRE-01 must reproduce or update these results. Do not rely on temporary files from the earlier review.

### Current source findings

Source inspection on 2026-11-13 identified these conditions:

| Finding | Evidence location or behavior | Tasks |
|---|---|---|
| Registration disablement does not cover gRPC mapping. | `MapWafishGrpc` maps the registration service unconditionally. | PRE-02, SEC-01 |
| Startup validation skips disabled registration. | `GatewayControlPlaneSecurityValidator` returns before checking authentication. | PRE-02, SEC-03 |
| Authentication fallback reads the wrong dashboard location. | `AddGatewayApiAuth` reads credentials outside `Gateway:Dashboard:Auth`. | PRE-02, SEC-02 |
| Empty dashboard API keys fall through to authorization success. | `DashboardAuthorizationService.IsAuthorizedAsync` returns true. | PRE-02, SEC-02 |
| Private responses can enter a shared cache. | `ResponseCacheMiddleware` lacks identity and private-response checks. | PRE-03, SEC-06 |
| Response middleware buffers entire responses. | Cache and compression use `MemoryStream` before applying size decisions. | SEC-07, SEC-08 |
| WAF inspection depends on Content-Length. | Unknown-length bodies skip inspection; body reading uses one read. | SEC-09 |
| MCP uses a separate unrestricted listener. | `McpServerHost` uses `ListenAnyIP` and wildcard origins without authentication. | SEC-05 |
| Persistence occurs after publication. | `ConfigManagerBase` publishes changes and catches persistence failures. | ARCH-06, ARCH-07 |
| Dashboard registration owns runtime services. | It starts configuration loading and registers circuit and destination services. | ARCH-02, ARCH-04 |
| Dashboard combines administration and presentation. | MVC Razor views, JavaScript, controllers, storage, AI, and MCP share one package. | ARCH-03, ARCH-05, UI-01 |
| Redis rate limiting allows requests when Redis fails. | `RedisLuaRateLimitStore` logs the failure and permits requests. | SEC-11, ASP-06 |
| Samples use fixed addresses and demo credentials. | Gateway and client settings configure ports, credentials, and wildcard listeners. | SEC-04, ASP-02, ASP-03 |
| The benchmark runner owns service processes. | It starts applications and polls fixed endpoint addresses. | ASP-07 |

No fresh build, regression run, or dependency audit accompanied this source inspection.

## Phase overview

| Phase | Status | Depends on | Completion requirement |
|---|---|---|---|
| Prerequisite: urgent fixes | DONE | None | Registration access and private-response isolation checks pass. |
| 1: Architectural improvements | DONE | Prerequisite | Runtime works without dashboard dependencies; persistence checks pass. |
| 2: Security fixes | DONE | Phase 1 | Security checks pass; SEC-14 resolves the native database release blocker. |
| 3: Aspire | DONE | Phase 2 | Local topology, telemetry, and recovery checks pass. |
| 4: UI | DONE | Phase 3 | Design is approved and replacement workflow checks pass. |

Tasks within a phase also depend on the preceding phase gate.
Dependencies listed below identify additional ordering within or across phases.

## Prerequisite: urgent fixes

### PRE-01 Establish a fresh baseline

Status: DONE. Dependencies: none. Owner: Codex. Completed: 2026-11-13.

Evidence: [baseline and reproduction results](tests/Wafish.Regression/evidence/README.md).

Build the solution and run the existing regression executable.
Record SDK versions, framework targets, commands, results, and the tested commit.
Create focused reproductions for registration access, empty API keys, and private-response leakage.
Record current dependency findings. Preserve evidence in the repository's test results or CI artifacts.

Acceptance: Each reported failure has a current reproduction or a documented reason it no longer occurs.

### PRE-02 Close administration access gaps

Status: DONE. Dependencies: PRE-01. Owner: Codex. Completed: 2026-11-13.

Evidence: SecurityChecks.cs verifies endpoint mapping, authentication configuration, and cache isolation.

Make registration disablement cover REST and gRPC endpoint mapping.
Correct authentication configuration paths and define configuration precedence.
Reject missing credentials for configured authentication modes, including empty dashboard API keys.
Test enabled and disabled registration across supported host configurations.

Acceptance: Disabled registration cannot mutate configuration. Anonymous and invalid requests cannot use protected administration endpoints.

### PRE-03 Prevent private-response sharing

Status: DONE. Dependencies: PRE-01. Owner: Codex. Completed: 2026-11-13.

Evidence: SecurityChecks.cs verifies endpoint mapping, authentication configuration, and cache isolation.

Check request eligibility before cache lookup. Bypass authenticated and cookie-bearing requests.
Reject private, no-store, and Set-Cookie responses before storage.
Add two-user tests that cover both cache population and subsequent cache hits.

Acceptance: Users cannot receive each other's responses. Eligible public responses still cache correctly.

### PRE-04 Upgrade to .NET 10

Status: DONE. Dependencies: none. Owner: Codex. Completed: 2026-11-13.

Evidence: Debug and Release solution builds passed with zero warnings. All 42 regression checks pass.
See [verification details](tests/Wafish.Regression/evidence/README.md).

Target .NET 10 across all projects. Update SDK selection, Docker images, SQLite packages, and developer tooling.
Update supported-framework documentation. Preserve historical results as historical evidence.

Acceptance: Debug and Release builds pass. Regression results identify pre-existing failures separately from migration failures.

## Phase 1: architectural improvements

### ARCH-01 Define package ownership

Status: DONE. Dependencies: prerequisite gate. Owner: Codex. Completed: 2026-11-13.

Evidence: [package ownership and target APIs](docs/architecture.md).

Record the dependency graph for runtime, administration services, shared contracts, storage, plugins, and presentation.
Assign every dashboard service to its owning layer before moving code.
Define public registration and mapping APIs. Keep runtime dependencies directed away from presentation.
Record process separation only as [IDEA-01](IDEAS.md#idea-01-separate-administration-and-gateway-processes).

Acceptance: Every service has an owner. The proposed graph has no runtime dependency on dashboard presentation.

### ARCH-02 Move runtime services

Status: DONE. Dependencies: ARCH-01. Owner: Codex. Completed: 2026-11-13.

Evidence: RuntimeHostChecks starts a gateway and forwards requests without dashboard registration.
It checks assembly dependencies and duplicate configuration startup registration.

Move configuration startup, circuit state, rate-limit state, and destination coordination into their runtime owners.
Preserve service lifetimes and avoid duplicate hosted-service registration.

Acceptance: Runtime startup loads configuration and resolves required services without registering the dashboard.

### ARCH-03 Separate administration from presentation

Status: DONE. Dependencies: ARCH-01, ARCH-02. Owner: Codex. Completed: 2026-11-14.

Evidence: [ARCH-03 verification](tests/Wafish.Regression/evidence/arch03/README.md).
All 102 HTTP contracts are preserved. Both regression executables and published browser checks pass.
Administration runs without the dashboard assembly, Razor services, pages, or static assets.

Extract administration services, HTTP endpoints, realtime contracts, and shared models from Razor presentation.
Keep runtime and administration within one host. Make UI registration optional.
Adapt the current Razor UI to the new boundary.

Acceptance: Administration endpoints work without Razor assets. Existing UI workflows use the separated administration layer.

### ARCH-04 Separate middleware and plugin activation

Status: DONE. Dependencies: ARCH-02, ARCH-03. Owner: Codex. Completed: 2026-11-14.

Evidence: runtime host tests verify configured caching, transforms, health endpoints, and endpoint-role isolation.

Move gateway pipeline composition and runtime plugin activation out of dashboard middleware setup.
Preserve middleware ordering, plugin execution, health checks, and deployment-mode behavior.

Acceptance: Proxy-only operation supports configured plugins without dashboard registration or assets.

### ARCH-05 Make AI and MCP optional

Status: DONE. Dependencies: ARCH-03. Owner: Codex. Completed: 2026-11-14.

Evidence: administration checks confirm explicit, independent, idempotent AI and MCP registration.
Configuration alone cannot register services or map AI endpoints.

Separate AI and MCP startup from general dashboard registration.
Require explicit activation. Keep shared administration access behind the same authorization boundary.

Acceptance: Disabled features create no listeners or background activity. Enabled features resolve through explicit registrations.

### ARCH-06 Persist configuration before activation

Status: DONE. Dependencies: ARCH-02, ARCH-03. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/Wafish.Regression/evidence/arch04-09/README.md).

Validate a candidate configuration without mutating active state.
Persist related changes transactionally before publishing the candidate.
Remove swallowed persistence failures from successful update paths.
Extend storage interfaces where necessary and preserve stale-version rejection.

Acceptance: Failed writes leave stored and active configuration unchanged. Successful updates survive restart.

### ARCH-07 Report update outcomes

Status: DONE. Dependencies: ARCH-06. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/Wafish.Regression/evidence/arch04-09/README.md).

Distinguish validation rejection, version conflict, storage failure, and activation failure in operation results.
Report persisted and active versions when activation fails after a successful commit.
Expose actionable errors through administration APIs and the existing UI.

Acceptance: Clients cannot mistake an unsaved or unapplied change for a fully successful update.

### ARCH-08 Resolve existing runtime regressions

Status: DONE. Dependencies: PRE-01, ARCH-04, ARCH-06. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/Wafish.Regression/evidence/arch04-09/README.md).

Fix confirmed plugin activation, request-draining, and plugin-state persistence failures.
Preserve active plugin domains when replacement initialization fails.
Verify disposal waits for in-flight requests and persisted state survives restart.

Acceptance: Reproduced baseline failures pass without weakening the regression assertions.

### ARCH-09 Publish migration instructions

Status: DONE. Dependencies: ARCH-02, ARCH-03, ARCH-04, ARCH-05, ARCH-07, ARCH-08. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/Wafish.Regression/evidence/arch04-09/README.md).

Update samples, package references, registration methods, and configuration examples.
Document changed public APIs, configuration options, operation results, and storage interfaces.
Provide old-to-new examples and identify changes required by package consumers.
Target .NET 10 in libraries, samples, tests, and developer tooling.

Acceptance: Samples build and run using documented APIs. The existing Razor UI remains usable.

### ARCH-10 Preserve route activation and native configuration fields

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: ARCH-06, UI-05.3.

Found during UI verification: disabled routes lost their state after storage reload.
Migration 018 persists activation. Rename operations preserve native fields and route identities.
Exports, snapshots, imports, comparisons, and rollback include activation state.
Existing database rows default to enabled because earlier versions did not persist activation.

Acceptance: Rename/restart and snapshot/import/rollback checks preserve disabled routes and native configuration.
Evidence: [UI integration verification](tests/Wafish.Regression/evidence/ui/README.md).

## Phase 2: security fixes

### SEC-01 Apply consistent administration authentication

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Apply the administration policy across REST, gRPC, dashboard APIs, and realtime endpoints.
Cover endpoint mapping, request handling, and configuration changes.

Acceptance: Missing and invalid credentials fail on every protected transport. Valid credentials permit intended operations.

### SEC-02 Validate modes and credentials

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-01.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Reject unsupported modes, empty credentials, and unintended authentication-method fallback.
Make configuration precedence explicit. Restrict URL tokens to exact endpoints that require them.

Acceptance: Invalid configuration fails clearly. A credential for another mode cannot bypass the selected mode.

### SEC-03 Define local and production access policies

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-01, SEC-02.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Require authentication by default. Allow anonymous development access only through an explicit loopback restriction.
Require HTTPS for remote administration. Apply policy to all enabled administration listeners.
Test Development, Staging, and Production. Environment names alone must not establish network safety.

Acceptance: Unsafe configurations are rejected. Documented loopback development scenarios remain usable.

### SEC-04 Remove insecure deployment defaults

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-03.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Remove production reliance on sample credentials and secret-bearing startup output.
Verify listener bindings, endpoint roles, and forwarded-header trust.
Document secret injection and safe sample startup.

Acceptance: Production examples contain no working demo credentials. Spoofed forwarding headers cannot grant trusted access.

### SEC-05 Secure MCP

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: ARCH-05, SEC-03.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Default MCP to loopback binding. Authenticate requests and validate origins.
Apply the parent host's security policy to the separate MCP server.
Keep MCP disabled until its security checks pass.

Acceptance: Unauthorized requests and untrusted browser origins fail. Allowed clients can complete supported operations.

### SEC-06 Complete cache correctness

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: PRE-03.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Handle cache directives, Vary, host separation, query variation, and GET/HEAD semantics.
Invalidate affected entries when route configuration changes.
Test middleware ordering with compression and authorization.

Acceptance: Private responses remain isolated. Public cached responses preserve correct headers, bodies, and request variants.

### SEC-07 Bound cache memory

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-06.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Stop response capture at its byte limit and continue streaming without losing captured bytes.
Add a dedicated total cache budget with entry accounting and eviction.
Avoid full-response copies after the size limit is exceeded.

Acceptance: Large and concurrent responses respect configured memory budgets without truncation or cancellation leaks.

### SEC-08 Stream compressed responses

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-06, SEC-07.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Replace full-response compression buffering with streaming or bounded processing.
Preserve encoding negotiation, Vary, range handling, bodyless responses, and cancellation.

Acceptance: Large responses stream correctly. Compressed and uncompressed cache variants cannot be confused.

### SEC-09 Enforce request inspection limits

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Inspect bodies without Content-Length. Count bytes and handle short reads until the inspection boundary.
Enforce body limits and define overflow behavior explicitly.
Preserve readable bodies for downstream handlers. Handle cancellation and inspection errors explicitly.

Acceptance: Chunked requests cannot bypass limits or required inspection. Valid request bodies reach downstream handlers intact.

### SEC-10 Resolve dependency findings

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: PRE-01.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Current evidence: the .NET 10 package audit reports no known package vulnerabilities.
The initial encrypted-storage review loaded SQLCipher 4.5.2 with SQLite 3.39.2.
That review identified a native dependency blocker. SEC-14 now resolves it with SQLite3MC 2.7.0 and SQLite 3.54.1.

Audit direct and transitive dependencies. Verify the SQLite native library actually loaded at runtime.
Select compatible updates and preserve SQLCipher behavior.
Record advisory resolution and any remaining exposure.

Acceptance: Dependency checks pass or identify an explicit release blocker. Encrypted database operations and restart checks pass.

### SEC-11 Define Redis failure behavior

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/Wafish.Regression/evidence/security/README.md).

Make rate-limit failure behavior explicit and configurable.
Document whether each policy permits or rejects requests when Redis becomes unavailable.
Add outage and recovery tests without assuming that logging alone provides enforcement.

Acceptance: Requests follow the configured policy during outages. Recovery restores shared counters and expected limits.

### SEC-12 Remove registration client TLS bypass

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-04.

Removed unconditional certificate acceptance discovered during Aspire endpoint wiring.
ClientTlsChecks uses a self-signed HTTPS server and verifies rejection.
Platform certificate validation now applies in every environment.

### SEC-13 Preserve two-factor authentication on persistence failures

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-01.

Two-factor state now uses atomic replacement and owner-only Unix file permissions.
Corrupt or unreadable state rejects login. Failed writes return HTTP 503.
The state path is configurable per gateway. JWT password comparison uses constant-time hashes.
Administration regression checks cover reload, permissions, corruption, failed writes, and rejected login without session cookies.

### SEC-14 Replace the retired native database component

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-10.

Evidence: [native storage replacement](tests/Wafish.Regression/evidence/sec14/README.md).

Replace the deprecated SQLCipher bundle with maintained SQLite3MC binaries.
Keep plaintext databases unchanged. Select SQLCipher 4 compatibility for existing password-protected file connections.
Verify loaded native versions, old database fixtures, migrations, writes, reopen, and invalid-key rejection.
Route database backups through the same factory and reject failed backups without copying a live database file.
Verify committed WAL data and encryption in downloaded backups.

Acceptance: Patched native engine loads in tests and the published sample; legacy plaintext and encrypted data remain readable.
No retired native library remains in deployment artifacts. Configuration defaults do not enable encryption.

## Phase 3: Aspire

### ASP-01 Add the development AppHost

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: phase 2 gate.

Create one AppHost for the sample gateway and two backend instances.
Keep orchestration dependencies in sample and test hosts.
Retain the gateway's local SQLite file with an explicit persistent path.

Acceptance: One documented command starts the sample topology without modifying reusable gateway package dependencies.

### ASP-02 Wire explicit endpoints

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-01.

Replace fixed sample addresses with Aspire endpoint references.
Configure HTTP and gRPC independently. Preserve required HTTP/2 behavior.
Remove conflicts with automatic Kestrel port selection.

Acceptance: Services communicate through resolved endpoints without fixed-port assumptions.

### ASP-03 Configure authenticated registration

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-02.

Enable registration explicitly for the sample topology and inject credentials securely.
Give each backend a distinct destination identity within the intended shared cluster.

Acceptance: Both backends register correctly. Invalid credentials fail. Restart does not create duplicate destinations.

### ASP-04 Add health checks and telemetry

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-03.

Configure readiness, startup dependencies, logs, metrics, and correlated request traces.
Keep gateway readiness independent of backends that must register after startup.
Avoid resilience defaults that alter existing retry behavior.

Acceptance: Startup has no dependency cycle. Telemetry identifies the gateway and destination for a request.

### ASP-05 Add optional Redis

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-01, SEC-11.

Add a Redis resource for distributed rate-limit scenarios.
Pass its connection information into the plugin's actual configuration path.
Keep the default SQLite storage choice unchanged.

Acceptance: Redis scenarios use the managed resource. The basic sample runs without Redis.

### ASP-06 Verify topology recovery

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-03, ASP-04, ASP-05.

Test load balancing, registration, shutdown, restart, backend failure, and Redis failure.
Verify shared rate limits with multiple gateway instances using isolated configuration stores when required.

Acceptance: Both backends receive traffic. Recovery restores routing and the configured rate-limit policy.

### ASP-07 Add external-endpoint test execution

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-02, ASP-04.

Add a performance-runner mode that accepts externally managed endpoints.
In this mode, the runner must not start or terminate service processes.
Preserve the existing benchmark path and comparable telemetry settings.

Acceptance: Both runner modes work. External mode leaves AppHost-managed services running after completion.

### ASP-08 Document local operation

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/Wafish.Regression/evidence/aspire/README.md). Dependencies: ASP-06, ASP-07.

Document startup, shutdown, endpoints, credentials, troubleshooting, and persistent-data handling.
Explain the roles of the Aspire dashboard and gateway administration UI.

Acceptance: A fresh checkout can follow the instructions and reproduce the verified topology.

## Phase 4: UI

### UI-01 Inventory current workflows

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: inventory prepared alongside Aspire verification.

Evidence: [workflow inventory and API mapping](docs/ui-design.md).

Existing issues: the sidebar settings link can be obscured at the default browser viewport.
During ARCH-03, the Monaco worker language file returned HTTP 404.
The pre-extraction sample has the same issue. Cluster saving still succeeds.

Inventory screens, navigation, editors, plugins, authentication, history, logs, metrics, AI, and MCP controls.
Record realtime behavior and English localization requirements.
Identify mandatory workflows and candidates for simplification.

Acceptance: Every existing workflow has a retain, replace, or remove proposal.

### UI-02 Prepare the design options

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-01.

Evidence: [design comparison](docs/ui-design.md) and [interactive wireframes](docs/ui-wireframes.html).

Compare framework options, including retained Razor and alternative clients.
Prepare navigation proposals, wireframes, accessibility requirements, and delivery tradeoffs.
Map each proposed screen to available administration contracts.

Acceptance: The user can compare concrete designs and understand their scope and migration cost.

### UI-03 Record the UI design decision

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-02.

Decision: user selected React with shadcn/ui. See docs/ui-design.md.

Obtain the user's choice of framework, component system, workflows, and migration approach.
Record accepted wireframes and measurable usability requirements.

Acceptance: The design decision is recorded. No framework choice is left implicit.

### UI-04 Expand the replacement implementation plan

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-03.

Evidence: [approved implementation tasks](docs/ui-implementation.md).

Selected stack: React, TypeScript, Vite, Tailwind, shadcn/ui, React Router, TanStack Query, React Hook Form, Zod.

Define components, API consumption, state handling, errors, authorization, realtime updates, and migration steps.
Add stable child task identifiers for approved implementation work.

Acceptance: The approved replacement can be implemented without unresolved product decisions.

### UI-05 Implement the selected UI

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-04.

Evidence: [UI verification](tests/Wafish.Regression/evidence/ui/README.md).

Child tasks: UI-05.1 package and shell; UI-05.2 authentication; UI-05.3 configuration; UI-05.4 plugins; UI-05.5 observation; UI-05.6 history; UI-05.7 settings and integrations.

Implement the approved interface against administration contracts.
Keep runtime behavior outside presentation. Follow the agreed migration and fallback approach.

Acceptance: Approved screens and workflows work without direct gateway-runtime dependencies.

### UI-06 Verify functional workflows

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05.

Evidence: [UI verification](tests/Wafish.Regression/evidence/ui/README.md).

Test authentication, configuration editing, validation, plugins, history, logs, and realtime updates.
Include authorization failures, version conflicts, storage failures, and activation failures.

Acceptance: Browser checks cover successful operations and actionable failure states.

### UI-07 Verify accessibility and localization

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05.

Evidence: [UI verification](tests/Wafish.Regression/evidence/ui/README.md).

Verify keyboard access, focus behavior, responsive layouts, and English localization.
Check the selected design against its recorded accessibility requirements.

Acceptance: Approved accessibility and localization checks pass. Remaining limitations are recorded explicitly.

### UI-08 Remove the old Razor interface

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05, UI-06, UI-07.

Evidence: [Razor removal verification](tests/Wafish.Regression/evidence/ui08/README.md).

User decision: remove the old Razor UI completely unless it serves a required purpose.
The presentation package has no remaining purpose. Shared administration services remain necessary for React.
Remove the Razor project, pages, assets, registration wrappers, and fallback controls.
Migrate sample and performance hosts to explicit runtime and administration registration.
Keep authentication, configuration, plugin APIs, and realtime contracts.
Update package publishing, documentation, and page-contract expectations.

Acceptance: React workflows pass. The published sample contains no Razor UI package or assets.
Former page routes are unavailable. All 87 non-page controller contracts remain unchanged.

## Review follow-up: approved items 1–16

Original phase completion remains historical evidence. These tasks address additional review findings.

| Task | Status | Owner | Scope and next step |
|---|---|---|---|
| REV-01 | DONE | Codex | Authentication attempt limits, CIDR validation, WAF log sanitization, and HTTPS cookie checks pass. |
| REV-02 | DONE | Codex | Safe replay, bounded response and decoded capture, partition admission, and request resource rules pass. |
| REV-03 | DONE | Codex | Live Kestrel/YARP streaming, plugin combinations, and disconnect checks pass. |
| REV-04 | DONE | Codex | Effective benchmark policies, readiness, failure exit codes, and response deadlines pass. |
| REV-05 | DONE | Codex | Shared publication ownership, atomic binding batches, and revision-safe recovery pass. |
| REV-06 | DONE | Codex | Selected circuit probes, Redis policy isolation, and owned limiter lifetimes pass. |
| REV-07 | DONE | Codex | Awaited heartbeat shutdown, instance updates, and stable discovery identities pass. |
| REV-08 | DONE | Codex | Unfinished log batches and notification losses have complete accounting. |
| REV-09 | DONE | Codex | Indexed UTC comparisons and final-batch orphan cleanup pass. |
| REV-10 | DONE | Codex | Failed and cancelled metric attempts finalize once; retry semantics are documented. |
| REV-11 | DONE | Codex | PathBase, nested UI/API prefixes, frontend inputs, and package assets pass verification. |
| REV-12 | DONE | Codex | Retired domains and watcher tasks remain owned until shutdown completes. |
| REV-13 | DONE | Codex | Cache setting deprecations and bounded compression thresholds have explicit, tested behavior. |
| REV-14 | DONE | Codex | Package validation, first-release API snapshots, and isolated native consumers pass. User-approved API snapshots define compatibility until publication. |
| REV-15 | DONE | Codex | Live Redis, strict dependency auditing, JSON results, and retained CI artifacts are configured and checked locally. |
| REV-16 | DONE | Codex | UTC migration, controlled policy clocks, and bounded metric identity retention pass. |

Completed 2026-11-14. [Verification and compatibility evidence](tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted.

Items 4–7 completed 2026-11-14. [Verification and compatibility evidence](tests/Wafish.Regression/evidence/review4567/README.md).

Items 8–10 completed 2026-11-14. [Verification and compatibility evidence](tests/Wafish.Regression/evidence/review8910/README.md).

Items 11–13 completed 2026-11-14. [Verification and compatibility evidence](tests/Wafish.Regression/evidence/review111213/README.md).

Items 14–16 completed 2026-11-14. [Verification and compatibility evidence](tests/Wafish.Regression/evidence/review141516/README.md).
Linux validation is complete. Windows and macOS execution is configured in CI and awaits hosted runs.
User decision: treat packages as first releases. Reviewed API snapshots establish compatibility until publication.

## Execution record

| Date | Task | Owner | Status | Evidence and next action |
|---|---|---|---|---|
| 2026-11-14 | Waf IMP-01/02; Cache IMP-01; Compression IMP-01; Metrics IMP-01 | Codex | DONE | Generated protocol checks, six WAF false-positive fixes, mixed traversal rejection, and strict encoding qualities. 214 main and 35 administration cases pass; changed coverage 100%. [Evidence](tests/Wafish.Regression/evidence/protocol-properties/README.md). |
| 2026-11-14 | Regression IMP-01/02, FEAT-01/02; ReactUi.Host IMP-02, FEAT-01/02; ReactUi IMP-01/02 | Codex | DONE | Registry, replay traces, isolated profiles, audit merge, realtime updates, and workspace states. 209 main, 35 administration, 81 frontend, and 30 browser tests pass. [Evidence](tests/Wafish.Regression/evidence/regression-infrastructure/README.md). |
| 2026-11-14 | Administration IMP-01; Client FEAT-02; Grpc IMP-01/02; Plugin.Abstractions IMP-01/02 | Codex | DONE | Shared mutation responses, structured registration, wire/API compatibility, and lifecycle ownership. 200 main, 35 administration, and 23 browser cases pass. [Evidence](tests/Wafish.Regression/evidence/mutation-contracts/README.md). |
| 2026-11-14 | Runtime IMP-01/02; CircuitBreaker IMP-01/02; RateLimit IMP-01/02; Administration IMP-02; ProxyLog IMP-02 | Codex | DONE | Lease ownership, collectible unload, probe contention, limiter churn, and shutdown accounting. 187 main and 34 administration cases pass. [Evidence](tests/Wafish.Regression/evidence/lifetime-stress/README.md). |
| 2026-11-14 | Sqlite IMP-01, IMP-02, FEAT-01, FEAT-02; Storage.Abstractions IMP-01, IMP-02 | Codex | DONE | Migration faults, WAL recovery, bounded queries, provider conformance, and offline inspection/restore verification. 178 main and 33 administration cases pass. [Evidence](tests/Wafish.Regression/evidence/storage-reliability/README.md). |
| 2026-11-14 | ReactUi.Host IMP-01, IMP-03; Administration.Regression IMP-02, FEAT-01, FEAT-02 | Codex | DONE | HTTPS fixtures, cleanup ownership, full authorization matrix, and session revocation. 168 main, 32 administration, and 23 browser cases pass. [Evidence](tests/Wafish.Regression/evidence/administration-lifecycle/README.md). |
| 2026-11-14 | Core IMP-01, IMP-02, IMP-04; Retry IMP-01, IMP-02, FEAT-02 | Codex | DONE | Async preparation, cancellation ownership, generated concurrency checks, Retry-After, and total deadlines. 168 main and 24 administration cases pass. [Evidence](tests/Wafish.Regression/evidence/async-retry/README.md). |
| 2026-11-14 | Redis IMP-01, IMP-02, IMP-04; ServiceDiscovery IMP-01, IMP-02, IMP-04, FEAT-02 | Codex | DONE | Bounded connection recovery, deployment isolation, interrupted Lua, provider parsing, per-binding health, backoff, and stale policies. 159 regression cases and seven live Redis groups pass. [Evidence](tests/Wafish.Regression/evidence/recovery/README.md). |
| 2026-11-14 | REV-14 through REV-16 | Codex | DONE | Release build, 169 standard regression checks, live Redis, 20 browser checks, and three isolated consumers pass. [Evidence](tests/Wafish.Regression/evidence/review141516/README.md). |
| 2026-11-14 | REV-11 through REV-13 | Codex | DONE | Release build, 163 regression executions, 20 browser checks, and package checks pass. Changed C# coverage: 93.85%. [Evidence](tests/Wafish.Regression/evidence/review111213/README.md). |
| 2026-11-14 | REV-08 through REV-10 | Codex | DONE | Release build and 157 regression executions pass. Changed source coverage: 86.67%. [Evidence](tests/Wafish.Regression/evidence/review8910/README.md). |
| 2026-11-14 | REV-04 through REV-07 | Codex | DONE | Release build; 151 regression checks and 23 managed scenarios pass. Changed source coverage: 89.71%. [Evidence](tests/Wafish.Regression/evidence/review4567/README.md). |
| 2026-11-14 | REV-01 through REV-03 | Codex | DONE | Release solution build; 110 main and 24 administration checks pass. Changed source coverage: 91.64%. [Evidence](tests/Wafish.Regression/evidence/review123/README.md). |
| 2026-11-14 | SEC-14 | Codex | DONE | Maintained SQLite3MC 2.7.0 / SQLite 3.54.1; legacy plaintext and SQLCipher compatibility verified. 68 regression, 20 administration, and 17 browser checks pass. Published encryption and backups verified. |
| 2026-11-14 | UI-08 | Codex | DONE | Removed Razor project, pages, assets, wrappers, and fallback. All 87 API contracts remain; 64 regression, 19 administration, nine frontend, and 17 browser checks pass. Published sample and ProxyOnly isolation verified. |
| 2026-11-14 | ASP-01 through ASP-08 | Codex | DONE | Dynamic endpoints, authenticated multi-instance registration, telemetry, Redis outage/recovery, and both runner modes verified. |
| 2026-11-14 | UI-03 through UI-07, ARCH-10, SEC-13 | Codex | DONE | React/shadcn workspace, persistent activation, and safe two-factor storage verified. Release build, 64 main, 19 administration, nine frontend, and 16 browser checks pass. |
| 2026-11-14 | SEC-01 through SEC-11 | Codex | DONE | Authentication, safe samples, MCP, bounded cache, streaming compression, WAF, Redis recovery verified. Native SQLCipher remains a release blocker. Next: Aspire. |
| 2026-11-14 | ARCH-04 through ARCH-09 | Codex | DONE | Pipeline separation, explicit AI/MCP, atomic updates, structured errors, lifecycle checks, and migration notes. Security work started. |
| 2026-11-14 | ARCH-03 | Codex | DONE | Administration extracted; 102 HTTP contracts preserved; 44 main and eight API-only checks pass; browser workflows verified. Next: ARCH-04. |
| 2026-11-13 | ARCH-01, ARCH-02 | Codex | DONE | Boundaries documented; runtime startup and proxy forwarding pass without dashboard registration. Next: ARCH-03 administration extraction. |
| 2026-11-13 | ARCH-08 | Codex | IN PROGRESS | All three baseline failures pass. Repeat lifecycle and persistence verification after ARCH-04 and ARCH-06. |
| 2026-11-13 | PRE-01 through PRE-04 | Codex | DONE | Baseline captured; .NET 10 builds, 42 regression checks, and published dashboard checks pass. Docker execution remains unverified because socket access was denied. |
| 2026-11-13 | Planning documents | Codex | DONE | Created this plan and IDEAS.md; added the README changelog. Start PRE-01 next. |

All phase acceptance checks are complete. SEC-14 resolves the native database blocker.
Live AI-provider and notification delivery checks still require deployment-specific credentials.
See [UI evidence](tests/Wafish.Regression/evidence/ui/README.md) for final verification and external integration limits.

For each completed task, add an evidence record with these fields:

- Task identifier and owner.
- Completion date and tested commit.
- Commands or manual checks performed.
- Results and artifact links.
- Remaining limitations and follow-up tasks.

## Reference guidance

- [ASP.NET Core response caching](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware?view=aspnetcore-10.0).
- [MCP transport security](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx).
- [SQLite advisory GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
- [Adding Aspire to an existing application](https://aspire.dev/get-started/add-aspire-existing-app/).

Check current guidance and installed package versions when implementing each task.

Priority 10 completed: [benchmark reliability evidence](tests/Wafish.Regression/evidence/benchmark-reliability/README.md).

Priority 11 completed: [allocation pressure evidence](tests/Wafish.Regression/evidence/allocation-pressure/README.md).

Priority 12 completed: [sample deployment evidence](tests/Wafish.Regression/evidence/sample-deployment/README.md).

Priority 13 completed: [sample contracts and final verification](tests/Wafish.Regression/evidence/sample-contracts/README.md).
Paused after priority 13. Resume only when requested.
