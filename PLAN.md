# Wafish remaining work

Updated: 2026-11-15.

This is the single active backlog. R01–R35 and the approved review fixes are complete.
Completed plans and design records are in the [planning archive](docs/archive/2026-11-planning/README.md).
Current behavior belongs in project READMEs and [product documentation](docs/guide/index.md).

## Priority and ratings

Value measures expected product or engineering benefit. Need measures urgency within the current release scope.
Both scores range from 1 (low) to 10 (essential).
Order accounts for dependencies, not only scores. Scores are judgments, not defect severity ratings.
The scope remains one gateway host with optional administration and UI.
Tenant isolation, mandatory passkeys, or central gateway management would change these priorities.
R36–R55 remain optional proposals. No feature becomes a release requirement because it appears here.
R56 records a release discrepancy found during cleanup; it does not authorize publication.

| Order | ID | Remaining task | Value / 10 | Need / 10 |
|---|---|---|---|---|
| 1 | R56 | [Reconcile the release version and package publication](#r56) | 9 | 9 |
| 2 | R43 | [Add matched HTTP/2 and TLS baselines](#r43) | 8 | 7 |
| 3 | R44 | [Schedule request arrival rates](#r44) | 8 | 7 |
| 4 | R42 | [Add paced streaming benchmarks](#r42) | 8 | 6 |
| 5 | R47 | [Benchmark selected plugin combinations](#r47) | 8 | 6 |
| 6 | R45 | [Enforce CI performance budgets](#r45) | 9 | 7 |
| 7 | R38 | [Combine burst and sustained quotas](#r38) | 8 | 6 |
| 8 | R36 | [Share local quotas across named route groups](#r36) | 7 | 6 |
| 9 | R40 | [Measure compression savings](#r40) | 7 | 6 |
| 10 | R41 | [Configure compression algorithms and levels](#r41) | 7 | 5 |
| 11 | R50 | [Demonstrate blue-green deployment](#r50) | 7 | 5 |
| 12 | R48 | [Benchmark multiple destinations](#r48) | 6 | 5 |
| 13 | R46 | [Add WebAuthn authentication](#r46) | 8 | 5 |
| 14 | R37 | [Partition quotas by trusted tenant](#r37) | 8 | 4 |
| 15 | R39 | [Assign weighted request costs](#r39) | 6 | 4 |
| 16 | R49 | [Add optional network fault injection](#r49) | 6 | 4 |
| 17 | R51 | [Demonstrate service discovery](#r51) | 6 | 4 |
| 18 | R55 | [Save operator views](#r55) | 5 | 3 |
| 19 | R52 | [Demonstrate cancellable streaming](#r52) | 5 | 3 |
| 20 | R53 | [Show redacted telemetry configuration](#r53) | 5 | 3 |
| 21 | R54 | [Demonstrate idempotent orders](#r54) | 4 | 2 |
| 22 | IDEA-01 | [Separate administration and gateway processes](#idea-01) | 9 | 3 |

## Completion rule

Complete each task only after its implementation and acceptance checks pass.
Record verification evidence and compatibility impact. Update the current documentation.
Retain task IDs when priorities change.
R45 follows representative measurements and repeatability checks; use R42–R44 and R47 where relevant.
Publication requires a separate release decision after checking the feed and artifact identity.

## Task details

<a id="r56"></a>

### R56: Reconcile the release version and package publication

Status: TODO. Value: 9/10. Need: 9/10.

The GitHub v1.0.0 tag differs from the candidate version in source and release notes.

Evidence: [GitHub release v1.0.0](https://github.com/hallojoe/wafish/releases/tag/v1.0.0),
[source version](src/Directory.Build.props), and [candidate release notes](docs/release-notes-1.0.0-rc.1.md).
The source and release notes still identify `1.0.0-rc.1`; the notes say publication is pending.
Package-feed state was not verified during cleanup.

Publishing automation is implemented: [workflow setup](docs/package-publishing.md).
Trusted-publisher setup, version reconciliation, and actual publication remain pending.

Acceptance:

- Verify the package feed, published versions, release tag, and associated source commit.
- Select the intended public version without replacing published packages or rewriting release history.
- Align package metadata, release notes, website installation examples, and release assets.
- Verify clean package consumers and supported-platform CI for the exact release artifacts.
- Record the published package baseline when publication is complete.
- Keep reviewed API snapshots authoritative until that baseline exists.

<a id="r43"></a>

### R43: Add matched HTTP/2 and TLS baselines

Status: TODO. Value: 8/10. Need: 7/10.

Establish fair HTTP/2 and TLS comparisons before performance claims.

Source: [NativeYarp FEAT-01](docs/archive/2026-11-planning/tests/Wafish.Performance/NativeYarp/PLAN.md#feat-01-add-selectable-http2-and-tls-baselines).

Measure transport overhead with explicit, matched client and upstream settings.

Acceptance: Reports compare equivalent transport modes and document certificate setup.

<a id="r44"></a>

### R44: Schedule request arrival rates

Status: TODO. Value: 8/10. Need: 7/10.

Expose overload instead of reducing offered load silently.

Source: [Runner FEAT-02](docs/archive/2026-11-planning/tests/Wafish.Performance/Runner/PLAN.md#feat-02-add-scheduled-request-rate-mode).

Evidence: [Program.cs](tests/Wafish.Performance/Runner/Program.cs) uses workers that wait for each response before sending another request.

Add an optional fixed arrival-rate mode with a bounded queue.
Report queue delay, dropped arrivals, completed responses, and service time separately.

Acceptance: A slow gateway does not silently reduce the offered request rate.
Overload remains visible without unbounded client memory.

<a id="r42"></a>

### R42: Add paced streaming benchmarks

Status: TODO. Value: 8/10. Need: 6/10.

Measure early delivery and buffering for streaming workloads.

Source: [Backend FEAT-01](docs/archive/2026-11-planning/tests/Wafish.Performance/Backend/PLAN.md#feat-01-add-paced-streaming-scenarios).

Provide chunked and event-stream responses with controlled chunk sizes and intervals.

Acceptance: The harness can measure first-byte latency and buffering without an external service.

<a id="r47"></a>

### R47: Benchmark selected plugin combinations

Status: TODO. Value: 8/10. Need: 6/10.

Measure the actual policy combinations used by gateways.

Source: [EnhancedYarp FEAT-01](docs/archive/2026-11-planning/tests/Wafish.Performance/EnhancedYarp/PLAN.md#feat-01-add-a-bounded-plugin-combination-matrix).

Generate selected plugin combinations with a recorded configuration for each run.

Acceptance: Reports attribute cost to explicit combinations and keep the matrix size controlled.

<a id="r45"></a>

### R45: Enforce CI performance budgets

Status: TODO. Value: 9/10. Need: 7/10.

Automate regression detection after stable, representative baselines exist.

Source: [Runner FEAT-01](docs/archive/2026-11-planning/tests/Wafish.Performance/Runner/PLAN.md#feat-01-add-ci-performance-regression-budgets).

Compare a run against a selected baseline with explicit tolerances.

Acceptance: The runner reports regressions separately from request failures and measurement noise.

<a id="r38"></a>

### R38: Combine burst and sustained quotas

Status: TODO. Value: 8/10. Need: 6/10.

Protect against short bursts and sustained excess traffic atomically.

Source: [Wafish.Plugin.RateLimit.Redis FEAT-02](docs/archive/2026-11-planning/src/Wafish.Plugin.RateLimit.Redis/PLAN.md#feat-02-combine-burst-and-sustained-quotas).

Evidence: [RedisLuaRateLimitStore.cs](src/Wafish.Plugin.RateLimit.Redis/RedisLuaRateLimitStore.cs) evaluates one quota per acquisition.

Support a short burst allowance and a longer sustained allowance in one atomic decision.
Define Redis Cluster key placement and expiry rules.

Acceptance: Exceeding either allowance rejects the request.
Rejected requests do not partially consume another allowance.

<a id="r36"></a>

### R36: Share local quotas across named route groups

Status: TODO. Value: 7/10. Need: 6/10.

Share one allowance across related routes.

Source: [Wafish.Plugin.RateLimit FEAT-01](docs/archive/2026-11-planning/src/Wafish.Plugin.RateLimit/PLAN.md#feat-01-support-named-quota-groups).

Let selected routes share an explicitly named local quota.

Acceptance: Grouped routes share permits while unrelated routes remain isolated.

<a id="r40"></a>

### R40: Measure compression savings

Status: TODO. Value: 7/10. Need: 6/10.

Measure whether compression produces useful savings.

Source: [Wafish.Plugin.Compression FEAT-01](docs/archive/2026-11-planning/src/Wafish.Plugin.Compression/PLAN.md#feat-01-expose-compression-savings-metrics).

Report input bytes, output bytes, and chosen encoding through bounded metric dimensions.

Acceptance: Operators can identify routes where compression increases cost without useful byte savings.

<a id="r41"></a>

### R41: Configure compression algorithms and levels

Status: TODO. Value: 7/10. Need: 5/10.

Control route CPU and bandwidth tradeoffs.

Source: [Wafish.Plugin.Compression FEAT-02](docs/archive/2026-11-planning/src/Wafish.Plugin.Compression/PLAN.md#feat-02-configure-allowed-compression-algorithms).

Evidence: [CompressionMiddleware.cs](src/Wafish.Plugin.Compression/CompressionMiddleware.cs), `SelectEncoding`, always considers Brotli and Gzip.

Allow route policy to select supported algorithms and their compression levels.
This permits explicit CPU and bandwidth tradeoffs.

Acceptance: Disabled algorithms are never selected.
Quality values, identity fallback, and Vary handling remain correct.

<a id="r50"></a>

### R50: Demonstrate blue-green deployment

Status: TODO. Value: 7/10. Need: 5/10.

Demonstrate safe deployment and rollback with existing APIs.

Source: [SampleGateway FEAT-01](docs/archive/2026-11-planning/samples/SampleGateway/PLAN.md#feat-01-add-a-blue-green-deployment-example).

Demonstrate a health-checked switch between two backend versions using existing configuration APIs.

Acceptance: The walkthrough proves cutover and rollback without losing the original route identity.

<a id="r48"></a>

### R48: Benchmark multiple destinations

Status: TODO. Value: 6/10. Need: 5/10.

Measure load balancing across matched destinations.

Source: [NativeYarp FEAT-02](docs/archive/2026-11-planning/tests/Wafish.Performance/NativeYarp/PLAN.md#feat-02-add-a-multiple-destination-baseline).

Evidence: [Program.cs](tests/Wafish.Performance/NativeYarp/Program.cs) configures one fixed destination.

Support a bounded configurable destination list and explicit YARP load-balancing policy.
Compare the same destinations and transport settings with EnhancedYarp.

Acceptance: Reports identify destination count and distribution.
The baseline keeps its independence from Wafish packages.

<a id="r46"></a>

### R46: Add WebAuthn authentication

Status: TODO. Value: 8/10. Need: 5/10.

Add passkeys when stronger operator login requirements justify the work.

Source: [Wafish.Administration FEAT-01](docs/archive/2026-11-planning/src/Wafish.Administration/PLAN.md#feat-01-add-webauthn-authentication).

Support registered authenticators as an additional administration login method.

Acceptance: Enrollment, challenge expiry, revocation, and recovery have integration tests.

<a id="r37"></a>

### R37: Partition quotas by trusted tenant

Status: TODO. Value: 8/10. Need: 4/10.

Raise priority when authenticated tenant quotas become a requirement.

Source: [Wafish.Plugin.RateLimit.Redis FEAT-01](docs/archive/2026-11-planning/src/Wafish.Plugin.RateLimit.Redis/PLAN.md#feat-01-add-trusted-tenant-quota-partitions).

Allow an explicitly configured, authenticated tenant claim to select the shared quota key.

Acceptance: Missing or untrusted claims follow a documented policy and cannot create unlimited key cardinality.

<a id="r39"></a>

### R39: Assign weighted request costs

Status: TODO. Value: 6/10. Need: 4/10.

Apply larger quota charges to expensive routes.

Source: [Wafish.Plugin.RateLimit FEAT-02](docs/archive/2026-11-planning/src/Wafish.Plugin.RateLimit/PLAN.md#feat-02-support-weighted-request-costs).

Evidence: [RateLimitMiddleware.cs](src/Wafish.Plugin.RateLimit/Middleware/RateLimitMiddleware.cs) always acquires one permit.

Allow validated route policy to assign a request cost for token and window algorithms.
Take costs from trusted configuration rather than caller headers.

Acceptance: An expensive route consumes its configured allowance.
Invalid costs fail validation, and rejection reports usable retry information.

<a id="r49"></a>

### R49: Add optional network fault injection

Status: TODO. Value: 6/10. Need: 4/10.

Make resilience demonstrations repeatable with controlled network failures.

Source: [Wafish.AppHost FEAT-01](docs/archive/2026-11-planning/Wafish.AppHost/PLAN.md#feat-01-add-optional-network-fault-injection).

Provide a local resource that can delay or interrupt backend traffic on request.

Acceptance: A repeatable scenario demonstrates retries and circuit recovery without changing production defaults.

<a id="r51"></a>

### R51: Demonstrate service discovery

Status: TODO. Value: 6/10. Need: 4/10.

Demonstrate discovery changes without changing the default sample.

Source: [Wafish.AppHost FEAT-02](docs/archive/2026-11-planning/Wafish.AppHost/PLAN.md#feat-02-add-a-service-discovery-demonstration-profile).

Evidence: [AppHost.cs](Wafish.AppHost/AppHost.cs) currently demonstrates registration through gRPC.

Add an opt-in HTTP JSON discovery catalog for the sample backends.
Exercise endpoint addition, removal, stale discovery, and recovery through real plugin bindings.

Acceptance: The default topology remains unchanged.
The discovery profile demonstrates stable destination identity and visible refresh failures.

<a id="r55"></a>

### R55: Save operator views

Status: TODO. Value: 5/10. Need: 3/10.

Reduce repeated filter and column setup for operators.

Source: [Wafish.ReactUi FEAT-01](docs/archive/2026-11-planning/src/Wafish.ReactUi/PLAN.md#feat-01-add-saved-operator-views).

Persist named filters and column choices through an authenticated per-user API.

Acceptance: Views restore after sign-in without storing credentials or sharing private preferences between users.

<a id="r52"></a>

### R52: Demonstrate cancellable streaming

Status: TODO. Value: 5/10. Need: 3/10.

Teach streaming cancellation through a bounded sample.

Source: [SampleLocalService FEAT-02](docs/archive/2026-11-planning/samples/SampleLocalService/PLAN.md#feat-02-demonstrate-cancellable-streaming).

Evidence: [LocalServiceController.cs](samples/SampleLocalService/Controllers/LocalServiceController.cs) returns complete responses.

Add a bounded streaming endpoint with configurable chunk count and interval.
Observe request cancellation and report only synthetic sample data.

Acceptance: The first chunk arrives before completion.
Disconnecting the client stops backend work.
The sample demonstrates cache, compression, and retry streaming behavior.

<a id="r53"></a>

### R53: Show redacted telemetry configuration

Status: TODO. Value: 5/10. Need: 3/10.

Make local telemetry setup easier to inspect.

Source: [SampleServiceDefaults FEAT-01](docs/archive/2026-11-planning/samples/SampleServiceDefaults/PLAN.md#feat-01-add-redacted-telemetry-diagnostics).

Expose an opt-in local diagnostic view of enabled exporters and instrumentation.

Acceptance: The view reports configuration state without revealing credentials or payload data.

<a id="r54"></a>

### R54: Demonstrate idempotent orders

Status: TODO. Value: 4/10. Need: 2/10.

Teach application idempotency; this extends the sample, not gateway behavior.

Source: [SampleLocalService FEAT-01](docs/archive/2026-11-planning/samples/SampleLocalService/PLAN.md#feat-01-add-an-idempotent-order-example).

Provide a sample write operation that demonstrates safe repeated requests with an idempotency key.

Acceptance: Repeated keys do not create duplicate sample orders, and conflicting payloads are rejected.

<a id="idea-01"></a>

### IDEA-01: Separate administration and gateway processes

Status: DEFERRED. Value: 9/10. Need: 3/10.

Defer until independent deployment or multi-gateway management is required.

#### Purpose

Run administration independently from gateway traffic processing.
This could support central management, independent scaling, and separate network access policies.

#### Current decision

Phase one separates packages while retaining runtime and administration within one host.
It makes the UI optional. It does not introduce a remote gateway-management protocol.
Preserve clean interfaces so process separation remains possible later.

#### Required design work

- Define gateway identity, enrollment, credential rotation, and revocation.
- Select an authenticated transport and authorize each management operation.
- Define configuration ownership, versioning, delivery, acknowledgment, and conflict handling.
- Distinguish persisted configuration from configuration active on each gateway.
- Define behavior during disconnection, partial rollout, and administration outages.
- Plan secure telemetry and audit attribution across process boundaries.
- Define topology, discovery, network exposure, health checks, and deployment requirements.
- Specify storage ownership and migration from the current single-host model.
- Version the management protocol and define compatibility between independently deployed components.

#### Promotion criteria

- A concrete requirement needs independent administration deployment or management of multiple gateways.
- Package separation and configuration persistence checks are complete.
- The user selects the operational model and accepts its additional complexity.
- Failure behavior, security boundaries, migration steps, and acceptance checks are documented.

#### Future acceptance scenarios

- An enrolled gateway receives and acknowledges an authorized configuration update.
- An unauthorized or revoked gateway cannot access management operations.
- A disconnected gateway follows an explicit last-known-configuration policy.
- Partial application across gateways is visible and recoverable.
- Every update identifies its actor, target gateway, persisted version, and active version.
- Administration restart does not interrupt gateway request processing.
