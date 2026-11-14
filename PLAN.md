# WafIsh improvement plan

Created: 2026-11-13. Last updated: 2026-11-14.

This file tracks architectural improvements, security fixes, Aspire integration, and UI work.
Complete urgent security fixes before architectural changes.
Keep each phase runnable and independently testable.

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
| Registration disablement does not cover gRPC mapping. | `MapWafIshGrpc` maps the registration service unconditionally. | PRE-02, SEC-01 |
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

Evidence: [baseline and reproduction results](tests/WafIsh.Regression/evidence/README.md).

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
See [verification details](tests/WafIsh.Regression/evidence/README.md).

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

Evidence: [ARCH-03 verification](tests/WafIsh.Regression/evidence/arch03/README.md).
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

Evidence: [architecture verification](tests/WafIsh.Regression/evidence/arch04-09/README.md).

Validate a candidate configuration without mutating active state.
Persist related changes transactionally before publishing the candidate.
Remove swallowed persistence failures from successful update paths.
Extend storage interfaces where necessary and preserve stale-version rejection.

Acceptance: Failed writes leave stored and active configuration unchanged. Successful updates survive restart.

### ARCH-07 Report update outcomes

Status: DONE. Dependencies: ARCH-06. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/WafIsh.Regression/evidence/arch04-09/README.md).

Distinguish validation rejection, version conflict, storage failure, and activation failure in operation results.
Report persisted and active versions when activation fails after a successful commit.
Expose actionable errors through administration APIs and the existing UI.

Acceptance: Clients cannot mistake an unsaved or unapplied change for a fully successful update.

### ARCH-08 Resolve existing runtime regressions

Status: DONE. Dependencies: PRE-01, ARCH-04, ARCH-06. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/WafIsh.Regression/evidence/arch04-09/README.md).

Fix confirmed plugin activation, request-draining, and plugin-state persistence failures.
Preserve active plugin domains when replacement initialization fails.
Verify disposal waits for in-flight requests and persisted state survives restart.

Acceptance: Reproduced baseline failures pass without weakening the regression assertions.

### ARCH-09 Publish migration instructions

Status: DONE. Dependencies: ARCH-02, ARCH-03, ARCH-04, ARCH-05, ARCH-07, ARCH-08. Owner: Codex. Completed: 2026-11-14.

Evidence: [architecture verification](tests/WafIsh.Regression/evidence/arch04-09/README.md).

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
Evidence: [UI integration verification](tests/WafIsh.Regression/evidence/ui/README.md).

## Phase 2: security fixes

### SEC-01 Apply consistent administration authentication

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Apply the administration policy across REST, gRPC, dashboard APIs, and realtime endpoints.
Cover endpoint mapping, request handling, and configuration changes.

Acceptance: Missing and invalid credentials fail on every protected transport. Valid credentials permit intended operations.

### SEC-02 Validate modes and credentials

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-01.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Reject unsupported modes, empty credentials, and unintended authentication-method fallback.
Make configuration precedence explicit. Restrict URL tokens to exact endpoints that require them.

Acceptance: Invalid configuration fails clearly. A credential for another mode cannot bypass the selected mode.

### SEC-03 Define local and production access policies

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-01, SEC-02.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Require authentication by default. Allow anonymous development access only through an explicit loopback restriction.
Require HTTPS for remote administration. Apply policy to all enabled administration listeners.
Test Development, Staging, and Production. Environment names alone must not establish network safety.

Acceptance: Unsafe configurations are rejected. Documented loopback development scenarios remain usable.

### SEC-04 Remove insecure deployment defaults

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-03.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Remove production reliance on sample credentials and secret-bearing startup output.
Verify listener bindings, endpoint roles, and forwarded-header trust.
Document secret injection and safe sample startup.

Acceptance: Production examples contain no working demo credentials. Spoofed forwarding headers cannot grant trusted access.

### SEC-05 Secure MCP

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: ARCH-05, SEC-03.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Default MCP to loopback binding. Authenticate requests and validate origins.
Apply the parent host's security policy to the separate MCP server.
Keep MCP disabled until its security checks pass.

Acceptance: Unauthorized requests and untrusted browser origins fail. Allowed clients can complete supported operations.

### SEC-06 Complete cache correctness

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: PRE-03.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Handle cache directives, Vary, host separation, query variation, and GET/HEAD semantics.
Invalidate affected entries when route configuration changes.
Test middleware ordering with compression and authorization.

Acceptance: Private responses remain isolated. Public cached responses preserve correct headers, bodies, and request variants.

### SEC-07 Bound cache memory

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-06.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Stop response capture at its byte limit and continue streaming without losing captured bytes.
Add a dedicated total cache budget with entry accounting and eviction.
Avoid full-response copies after the size limit is exceeded.

Acceptance: Large and concurrent responses respect configured memory budgets without truncation or cancellation leaks.

### SEC-08 Stream compressed responses

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: SEC-06, SEC-07.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Replace full-response compression buffering with streaming or bounded processing.
Preserve encoding negotiation, Vary, range handling, bodyless responses, and cancellation.

Acceptance: Large responses stream correctly. Compressed and uncompressed cache variants cannot be confused.

### SEC-09 Enforce request inspection limits

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Inspect bodies without Content-Length. Count bytes and handle short reads until the inspection boundary.
Enforce body limits and define overflow behavior explicitly.
Preserve readable bodies for downstream handlers. Handle cancellation and inspection errors explicitly.

Acceptance: Chunked requests cannot bypass limits or required inspection. Valid request bodies reach downstream handlers intact.

### SEC-10 Resolve dependency findings

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: PRE-01.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

Current evidence: the .NET 10 package audit reports no known package vulnerabilities.
The initial encrypted-storage review loaded SQLCipher 4.5.2 with SQLite 3.39.2.
That review identified a native dependency blocker. SEC-14 now resolves it with SQLite3MC 2.7.0 and SQLite 3.54.1.

Audit direct and transitive dependencies. Verify the SQLite native library actually loaded at runtime.
Select compatible updates and preserve SQLCipher behavior.
Record advisory resolution and any remaining exposure.

Acceptance: Dependency checks pass or identify an explicit release blocker. Encrypted database operations and restart checks pass.

### SEC-11 Define Redis failure behavior

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: phase 1 gate.

Evidence: [security verification](tests/WafIsh.Regression/evidence/security/README.md).

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

Evidence: [native storage replacement](tests/WafIsh.Regression/evidence/sec14/README.md).

Replace the deprecated SQLCipher bundle with maintained SQLite3MC binaries.
Keep plaintext databases unchanged. Select SQLCipher 4 compatibility for existing password-protected file connections.
Verify loaded native versions, old database fixtures, migrations, writes, reopen, and invalid-key rejection.
Route database backups through the same factory and reject failed backups without copying a live database file.
Verify committed WAL data and encryption in downloaded backups.

Acceptance: Patched native engine loads in tests and the published sample; legacy plaintext and encrypted data remain readable.
No retired native library remains in deployment artifacts. Configuration defaults do not enable encryption.

## Phase 3: Aspire

### ASP-01 Add the development AppHost

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: phase 2 gate.

Create one AppHost for the sample gateway and two backend instances.
Keep orchestration dependencies in sample and test hosts.
Retain the gateway's local SQLite file with an explicit persistent path.

Acceptance: One documented command starts the sample topology without modifying reusable gateway package dependencies.

### ASP-02 Wire explicit endpoints

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-01.

Replace fixed sample addresses with Aspire endpoint references.
Configure HTTP and gRPC independently. Preserve required HTTP/2 behavior.
Remove conflicts with automatic Kestrel port selection.

Acceptance: Services communicate through resolved endpoints without fixed-port assumptions.

### ASP-03 Configure authenticated registration

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-02.

Enable registration explicitly for the sample topology and inject credentials securely.
Give each backend a distinct destination identity within the intended shared cluster.

Acceptance: Both backends register correctly. Invalid credentials fail. Restart does not create duplicate destinations.

### ASP-04 Add health checks and telemetry

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-03.

Configure readiness, startup dependencies, logs, metrics, and correlated request traces.
Keep gateway readiness independent of backends that must register after startup.
Avoid resilience defaults that alter existing retry behavior.

Acceptance: Startup has no dependency cycle. Telemetry identifies the gateway and destination for a request.

### ASP-05 Add optional Redis

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-01, SEC-11.

Add a Redis resource for distributed rate-limit scenarios.
Pass its connection information into the plugin's actual configuration path.
Keep the default SQLite storage choice unchanged.

Acceptance: Redis scenarios use the managed resource. The basic sample runs without Redis.

### ASP-06 Verify topology recovery

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-03, ASP-04, ASP-05.

Test load balancing, registration, shutdown, restart, backend failure, and Redis failure.
Verify shared rate limits with multiple gateway instances using isolated configuration stores when required.

Acceptance: Both backends receive traffic. Recovery restores routing and the configured rate-limit policy.

### ASP-07 Add external-endpoint test execution

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-02, ASP-04.

Add a performance-runner mode that accepts externally managed endpoints.
In this mode, the runner must not start or terminate service processes.
Preserve the existing benchmark path and comparable telemetry settings.

Acceptance: Both runner modes work. External mode leaves AppHost-managed services running after completion.

### ASP-08 Document local operation

Status: DONE. Owner: Codex. Completed: 2026-11-14. Evidence: [Aspire verification](tests/WafIsh.Regression/evidence/aspire/README.md). Dependencies: ASP-06, ASP-07.

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

Evidence: [UI verification](tests/WafIsh.Regression/evidence/ui/README.md).

Child tasks: UI-05.1 package and shell; UI-05.2 authentication; UI-05.3 configuration; UI-05.4 plugins; UI-05.5 observation; UI-05.6 history; UI-05.7 settings and integrations.

Implement the approved interface against administration contracts.
Keep runtime behavior outside presentation. Follow the agreed migration and fallback approach.

Acceptance: Approved screens and workflows work without direct gateway-runtime dependencies.

### UI-06 Verify functional workflows

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05.

Evidence: [UI verification](tests/WafIsh.Regression/evidence/ui/README.md).

Test authentication, configuration editing, validation, plugins, history, logs, and realtime updates.
Include authorization failures, version conflicts, storage failures, and activation failures.

Acceptance: Browser checks cover successful operations and actionable failure states.

### UI-07 Verify accessibility and localization

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05.

Evidence: [UI verification](tests/WafIsh.Regression/evidence/ui/README.md).

Verify keyboard access, focus behavior, responsive layouts, and English localization.
Check the selected design against its recorded accessibility requirements.

Acceptance: Approved accessibility and localization checks pass. Remaining limitations are recorded explicitly.

### UI-08 Remove the old Razor interface

Status: DONE. Owner: Codex. Completed: 2026-11-14. Dependencies: UI-05, UI-06, UI-07.

Evidence: [Razor removal verification](tests/WafIsh.Regression/evidence/ui08/README.md).

User decision: remove the old Razor UI completely unless it serves a required purpose.
The presentation package has no remaining purpose. Shared administration services remain necessary for React.
Remove the Razor project, pages, assets, registration wrappers, and fallback controls.
Migrate sample and performance hosts to explicit runtime and administration registration.
Keep authentication, configuration, plugin APIs, and realtime contracts.
Update package publishing, documentation, and page-contract expectations.

Acceptance: React workflows pass. The published sample contains no Razor UI package or assets.
Former page routes are unavailable. All 87 non-page controller contracts remain unchanged.

## Execution record

| Date | Task | Owner | Status | Evidence and next action |
|---|---|---|---|---|
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
See [UI evidence](tests/WafIsh.Regression/evidence/ui/README.md) for final verification and external integration limits.

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
