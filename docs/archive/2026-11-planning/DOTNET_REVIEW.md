# .NET source review and proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../PLAN.md). Old statuses describe their original review date.

Review date: 2026-11-14.
Status: Approved review items 1–16 are complete. Other proposals retain their recorded status.
See [execution records and implementation evidence](PLAN.md#execution-record).

## Scope and evidence

This review covers all 30 .NET projects in [Wafish.slnx](../../../Wafish.slnx).
It covers libraries, samples, Aspire orchestration, regression applications, and performance tools.
The ReactUi review covers .NET hosting and asset packaging.
React component design and TypeScript behavior are outside this review.

Findings come from source, project files, existing plans, and CI workflows.
Source links identify the relevant implementation in each project plan.
Defects describe paths visible in source. They still require focused regression checks before fixes.
Performance proposals require measurements before implementation choices.

No .NET build, regression application, benchmark, or service was run for this documentation review.
No fresh dependency vulnerability audit was run.
Historical results in [PLAN.md](PLAN.md) are not current verification results.
The original documentation review did not change application code. Follow-up implementation has separate verification evidence.

## Existing foundations

- Projects target .NET 10 and use centrally managed package versions.
- Runtime, administration, plugin contracts, and storage have separate project boundaries.
- Configuration mutations validate candidates before storage and report storage and activation failures separately.
- Snapshot publication rejects stale versions.
- CI runs regression applications, frontend checks, and native storage checks across three operating systems.

Preserve these foundations when implementing the proposals.
Keep separate administration processes deferred under [IDEA-01](IDEAS.md#idea-01-separate-administration-and-gateway-processes).

## Priority guide

| Priority | Meaning |
|---|---|
| P1 | Correctness, security, availability, or misleading verification. Address before optional features. |
| P2 | Important reliability, maintainability, compatibility, or performance work. |
| P3 | Optional capability or later optimization. Validate demand first. |

Priorities describe recommended order. They do not create release commitments.

## Highest-priority findings

| Area | Finding | Detailed work |
|---|---|---|
| Configuration | Independent writers can consume a prepared snapshot version before activation. | [NET-IMP-01](#net-imp-01-coordinate-snapshot-commit-and-publication) |
| Retry | Requests can retry without replayable bodies. Responses use unbounded buffering. | [Retry IMP-03 and IMP-04](src/Wafish.Plugin.Retry/PLAN.md#imp-03-disable-retries-when-request-replay-is-unavailable) |
| Resource limits | Decoded log bodies and new limiter keys need immediate limits. | [ProxyLog plan](src/Wafish.Plugin.ProxyLog/PLAN.md), [RateLimit plan](src/Wafish.Plugin.RateLimit/PLAN.md) |
| Administration | Credential checks lack dedicated attempt limits. | [Administration plan](src/Wafish.Administration/PLAN.md) |
| Policy behavior | Circuit probes, Redis policy changes, and invalid CIDR input have unsafe paths. | [CircuitBreaker plan](src/Wafish.Plugin.CircuitBreaker/PLAN.md), [Redis plan](src/Wafish.Plugin.RateLimit.Redis/PLAN.md), [Waf plan](src/Wafish.Plugin.Waf/PLAN.md) |
| Data accuracy | Time comparisons can include old records. Batch deletion can retain orphaned clusters. | [SQLite plan](src/Wafish.Storage.Sqlite/PLAN.md), [Core plan](src/Wafish/PLAN.md) |
| Verification | Benchmark labels can differ from active policies. Managed failures can return success. | [EnhancedYarp plan](tests/Wafish.Performance/EnhancedYarp/PLAN.md), [Runner plan](tests/Wafish.Performance/Runner/PLAN.md) |

Project plans contain additional findings, evidence, feature ideas, and acceptance criteria.

## Solution-wide improvements

### NET-IMP-01: Coordinate snapshot commit and publication

Status: DONE. Priority: P1. Type: consistency gap.


Implemented 2026-11-14. [Verification and recovery contracts](../../../tests/Wafish.Regression/evidence/review4567/README.md).

Evidence:

- [DynamicConfigPublisher.cs](../../../src/Wafish/Services/DynamicConfigPublisher.cs) chooses a candidate version during preparation.
- [ConfigurationMutation.cs](../../../src/Wafish/Services/ConfigurationMutation.cs) stores the candidate before activation.
- [PluginBindingMutationService.cs](../../../src/Wafish.Runtime/Infrastructure/Plugin/PluginBindingMutationService.cs) uses a separate publication gate.
- [ServiceDiscoveryRefreshService.cs](../../../src/Wafish.Plugin.ServiceDiscovery/ServiceDiscoveryRefreshService.cs) uses its own publication lock.
- [GatewaySnapshotPublisher](../../../src/Wafish/Services/GatewaySnapshotCompiler.cs) rejects stale versions through compare-and-exchange.

The stale-version guard is correct, but independent preparation can create competing candidates.
A discovery refresh can publish while a configuration mutation awaits storage.
The stored candidate can then fail activation because its snapshot version is stale.
The mutation reports a persisted revision that requires restart before further updates.
This is a source-derived concurrency risk. Add a deterministic reproduction before changing coordination.

Introduce one coordination contract for configuration, binding, and discovery publication.
Define expected generations, preparation ownership, activation order, and recovery after storage succeeds.
Reject stale candidates before persistence where possible.
Rebuild or reconcile candidates when their dependencies change.
Do not remove the stale-version guard.
Keep stored configuration revisions distinct from runtime snapshot generations.
Prevent compensation from overwriting a newer successful operation.

Owners: Wafish, Runtime, ServiceDiscovery, Storage.Abstractions, and Storage.Sqlite.
The [atomic binding contract](src/Wafish.Storage.Abstractions/PLAN.md#imp-03-add-an-atomic-binding-batch-contract) supports this work.

Acceptance: Deterministic barriers interleave route changes, binding updates, and discovery refreshes.
Every successful change remains represented in the final active snapshot.
Concurrent publication alone does not leave a stored revision awaiting restart.
Storage failures leave active state unchanged.
Actual activation failures remain explicit and recoverable.

### NET-IMP-02: Validate published packages and consumer compatibility

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: release quality.

Implemented under review items 14–16. [Verification and limitations](../../../tests/Wafish.Regression/evidence/review141516/README.md).
The original finding and acceptance criteria follow.
User-approved baseline decision: treat packages as first releases and use reviewed API snapshots until publication.

Evidence: [src/Directory.Build.props](../../../src/Directory.Build.props) defines packaging without a package-validation baseline.
The [verification workflow](../../../.github/workflows/verify.yml) builds project references without isolated package consumers.

Add package validation for public libraries.
After publication, choose the baseline from an actual supported release.
Record intentional API changes and consumer migrations.
Package validation can compare public APIs against a baseline during packing.
See [Microsoft package validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview).

Build isolated consumers from produced packages with no repository project references.
Cover runtime-only hosting, administration hosting, plugin composition, and embedded React assets.
Check native SQLite loading and encryption on supported operating systems.
Preserve the single SQLite3MC native provider.

Owners: All packable src projects and CI.

Acceptance: Missing public dependencies, embedded assets, or native files fail the consumer checks.
An unintended public API removal fails compatibility validation.
Approved breaking changes include migration notes and an explicit baseline decision.

### NET-IMP-03: Define shared resource and cancellation limits

Status: DONE. Priority: P1. Type: availability.

Request replay, response capture, decoded logging, partition admission, and combined streaming checks are complete.
See [review items 1–3 evidence](../../../tests/Wafish.Regression/evidence/review123/README.md).
Queued-record loss accounting is complete. [Review items 8–10 evidence](../../../tests/Wafish.Regression/evidence/review8910/README.md).
Heartbeat shutdown ownership is complete under item 7. Retired-domain ownership is complete under item 12.
See [review items 11–13 evidence](../../../tests/Wafish.Regression/evidence/review111213/README.md).
Deadline policy and asynchronous configuration preparation are complete.
See [async policy evidence](../../../tests/Wafish.Regression/evidence/async-retry/README.md),
[lifetime evidence](../../../tests/Wafish.Regression/evidence/lifetime-stress/README.md), and
[allocation evidence](../../../tests/Wafish.Regression/evidence/allocation-pressure/README.md).
R01 verification repeated all three focused suites successfully on 2026-11-14.
Cancellation remains cooperative. Arbitrary plugin code and held requests cannot be forcibly terminated.
Configured deadlines bound owned waits; committed configuration reconciliation retains ownership.

Evidence: [Retry](src/Wafish.Plugin.Retry/PLAN.md), [ProxyLog](src/Wafish.Plugin.ProxyLog/PLAN.md), and [RateLimit](src/Wafish.Plugin.RateLimit/PLAN.md) expose different limit gaps.
[Client](src/Wafish.Client/PLAN.md) and [Runtime](src/Wafish.Runtime/PLAN.md) also need explicit background-task ownership.

Define common rules for request replay, response capture, decoded data, queued records, and partition counts.
Specify maximum retained bytes and behavior when each limit is reached.
Include plugin combinations because several plugins can buffer the same request.
Define request deadlines, shutdown deadlines, and ownership of disposable resources.
Allow cancellation during preparation without abandoning reconciliation after a successful commit.

Use asynchronous operations through the complete call path.
Measure large allocations and blocked worker threads under load.
See [ASP.NET Core best practices](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/best-practices?view=aspnetcore-10.0).

Owners: Runtime, Client, body-processing plugins, and regression projects.

Acceptance: Oversized bodies, compressed expansion, slow clients, and many partition keys retain bounded resources.
Cancellation releases owned resources and completes within the declared shutdown budget.
Retries never replay incomplete bodies or change responses after transmission starts.
Loss counters account for every discarded queued record.

### NET-IMP-04: Strengthen dependency and integration checks

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: verification coverage.

Implemented under review items 14–16. [Verification and limitations](../../../tests/Wafish.Regression/evidence/review141516/README.md).
The original finding and acceptance criteria follow.

Evidence: [Directory.Packages.props](../../../Directory.Packages.props) centralizes dependencies.
The [verification workflow](../../../.github/workflows/verify.yml) has no live Redis job or archived .NET dependency audit report.

.NET 10 projects already audit direct and transitive packages during restore by default.
Define explicit failure severity, audit-source availability handling, and report retention.
Avoid broad warning suppression.
See [Microsoft NuGet auditing guidance](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).
This review makes no claim about specific vulnerable dependency versions.

Add an isolated Redis service job for the existing Redis regression mode.
Cover algorithm changes, reconnection, concurrent callers, and unavailable-server behavior.
Add real-server tests for retry streaming and administration cookies.
Keep cross-platform SQLite checks and encrypted fixtures.
Publish machine-readable .NET results with case names and failure evidence.
Preserve console-runner entry points until any test-framework migration proves equivalent coverage.

Owners: Regression projects, performance tools, and CI.
Dependencies: Fix misleading benchmark outcomes before adding performance gates.

Acceptance: Redis failures and incorrect policy behavior fail CI.
Dependency audit failures cannot silently appear as a clean report.
Expected HTTP rejections remain distinct from connection failures.
CI artifacts identify the failing .NET scenario without requiring local reproduction.

### NET-IMP-05: Standardize stored time and controllable clocks

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: data consistency and testability.

Implemented under review items 14–16. [Verification and limitations](../../../tests/Wafish.Regression/evidence/review141516/README.md).
The original finding and acceptance criteria follow.

Proxy log UTC comparisons and migration 020 are complete under review item 9.
The wider clock and stored-time standardization is complete under review item 16.
Evidence: [SQLite plan](src/Wafish.Storage.Sqlite/PLAN.md).
[Core configuration](../../../src/Wafish/Services/ConfigurationMutation.cs) uses UTC timestamps, while other records use local time.

Define one UTC storage representation and one comparison rule.
Plan migration for existing text timestamps and offsets.
Use an injectable clock for leases, expiry, retry delays, circuit recovery, and cleanup.
Use monotonic elapsed time for durations.
Keep human display conversions outside persistence and policy logic.

Owners: Storage.Sqlite, Runtime, Client, time-dependent plugins, and regression projects.

Acceptance: Equivalent instants compare equally across offsets and daylight-saving changes.
Existing databases retain correct ordering after migration.
Expiry and recovery tests advance a controlled clock instead of waiting for real time.
Resolve the local SQLite comparison defect before the wider migration.

## Solution-wide feature suggestions

### NET-FEAT-01: Add versioned configuration change sets

Status: DONE. Owner: Codex. Completed: 2026-11-15.

Verification: Release builds, main `--group need-six`, administration `--need-six`, and existing configuration-change checks.
See [feature contracts](../../configuration-policy-contracts.md).

Evidence: Configuration and binding mutations currently have separate commit paths.
See [NET-IMP-01](#net-imp-01-coordinate-snapshot-commit-and-publication).

Offer one preview and apply operation for related route, cluster, and plugin changes.
Include an expected revision, normalized diff, validation results, and audit actor.
Report stored and active revisions separately.
Preserve existing authorization and secret-redaction rules.

Owners: Wafish, Storage.Abstractions, Storage.Sqlite, Runtime, Administration, and Client.
Dependencies: NET-IMP-01, atomic binding storage, and the core configuration preview.

Acceptance: A rejected change set modifies no configuration or binding records.
Concurrent edits return an explicit conflict with enough revision data to retry safely.
An accepted change set publishes a consistent configuration and records one correlated audit outcome.

### NET-FEAT-02: Add per-instance registration leases and draining

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [R19–R28 evidence](../../../tests/Wafish.Regression/evidence/need-seven/README.md).
See [feature contracts](../../registration-operations-contracts.md) for compatibility and operational limits.

Evidence: [gRPC plan](src/Wafish.Grpc/PLAN.md) records destination ownership gaps.
The [client plan](src/Wafish.Client/PLAN.md) records heartbeat and shutdown lifecycle gaps.
The previous heartbeat model did not define an expiring lease for each registered instance.

Negotiate a lease identifier, expiry, refresh interval, and grace period during registration.
Refresh only the authenticated instance's lease.
Expire crashed instances without removing healthy peers.
Allow graceful draining before planned removal.
Define compatibility behavior for clients without lease support.

Owners: Grpc, Client, Wafish, Runtime, Storage.Abstractions, and Storage.Sqlite.
Dependencies: Instance-owned destination updates and NET-IMP-01.

Acceptance: A crashed instance disappears after the configured grace period.
Another instance cannot refresh, replace, or remove its destinations.
Transient refresh failures do not remove healthy peers prematurely.
Graceful drain allows active requests to finish within a bounded deadline.

### NET-FEAT-03: Add a redacted diagnostic bundle

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [R19–R28 evidence](../../../tests/Wafish.Regression/evidence/need-seven/README.md).
See [feature contracts](../../registration-operations-contracts.md) for compatibility and operational limits.

Evidence: [Runtime](src/Wafish.Runtime/PLAN.md), [Metrics](src/Wafish.Plugin.Metrics/PLAN.md), and [ProxyLog](src/Wafish.Plugin.ProxyLog/PLAN.md) expose separate diagnostic concerns.

Export one bounded support bundle through an authorized administration operation.
Include build identity, active and stored revisions, plugin order, health, drain state, and queue-loss counters.
Include storage capability results and correlation identifiers.
Exclude credentials, secret configuration, request bodies, and connection strings.
Make collection cancellation-safe and record failed probes separately.

Owners: Administration, Runtime, Storage.Abstractions, and diagnostic plugins.

Acceptance: A fixture containing known secrets produces no secret values in the bundle.
An unavailable provider produces a partial bundle with explicit failures.
Bundle size and collection time remain bounded.

## Suggested execution order

1. Add focused reproductions for P1 findings and correct benchmark result reporting.
2. Fix retry safety, resource limits, authentication attempt limits, and invalid policy input.
3. Implement shared publication coordination with atomic binding storage and deterministic concurrency checks.
4. Fix lifecycle, timestamp, identity, and deployment-path findings in the project plans.
5. Add package-consumer validation and the missing integration checks.
6. Implement features after their stated prerequisites pass.

Independent project fixes can proceed without waiting for unrelated feature work.
Do not combine all proposals into one implementation change.

## Project coverage

Each project keeps its existing proposal identifiers.
New source-review sections extend those plans without marking earlier proposals complete.

| Project | Project-specific plan and main follow-up |
|---|---|
| Wafish | [Batch deletion, asynchronous preparation, configuration preview](src/Wafish/PLAN.md) |
| Wafish.Runtime | [Limiter ownership, plugin shutdown, drain diagnostics](src/Wafish.Runtime/PLAN.md) |
| Wafish.Administration | [Authentication limits, notification loss, operation permissions](src/Wafish.Administration/PLAN.md) |
| Wafish.Client | [Heartbeat lifecycle and structured outcomes](src/Wafish.Client/PLAN.md) |
| Wafish.Grpc | [Destination ownership and lease negotiation](src/Wafish.Grpc/PLAN.md) |
| Wafish.Storage.Abstractions | [Atomic binding batches and provider backup contracts](src/Wafish.Storage.Abstractions/PLAN.md) |
| Wafish.Storage.Sqlite | [Timestamp comparisons, snapshot costs, backup verification](src/Wafish.Storage.Sqlite/PLAN.md) |
| Wafish.Plugin.Abstractions | [Statistic contracts and pipeline ordering](src/Wafish.Plugin.Abstractions/PLAN.md) |
| Wafish.Plugin.Cache | [Configuration accuracy, retained buffers, concurrent fills](src/Wafish.Plugin.Cache/PLAN.md) |
| Wafish.Plugin.CircuitBreaker | [Probe permit ownership and maintenance windows](src/Wafish.Plugin.CircuitBreaker/PLAN.md) |
| Wafish.Plugin.Compression | [Threshold consistency and algorithm policy](src/Wafish.Plugin.Compression/PLAN.md) |
| Wafish.Plugin.Metrics | [Exception accounting, bounded identities, latency histograms](src/Wafish.Plugin.Metrics/PLAN.md) |
| Wafish.Plugin.ProxyLog | [Decoded limits, shutdown loss, disk quotas](src/Wafish.Plugin.ProxyLog/PLAN.md) |
| Wafish.Plugin.RateLimit | [Partition admission limits and request costs](src/Wafish.Plugin.RateLimit/PLAN.md) |
| Wafish.Plugin.RateLimit.Redis | [Algorithm key isolation, failed connections, multiple quotas](src/Wafish.Plugin.RateLimit.Redis/PLAN.md) |
| Wafish.Plugin.Retry | [Replay safety, bounded responses, retry deadlines](src/Wafish.Plugin.Retry/PLAN.md) |
| Wafish.Plugin.ServiceDiscovery | [Stable identities, error reporting, stale endpoint limits](src/Wafish.Plugin.ServiceDiscovery/PLAN.md) |
| Wafish.Plugin.Waf | [CIDR validation, log redaction, scoped exclusions](src/Wafish.Plugin.Waf/PLAN.md) |
| Wafish.ReactUi | [.NET PathBase handling, build inputs, asset versions](src/Wafish.ReactUi/PLAN.md) |
| Wafish.AppHost | [Isolated Sonar volumes and discovery demonstrations](Wafish.AppHost/PLAN.md) |
| SampleGateway | [Configured public URLs and a minimal runtime profile](samples/SampleGateway/PLAN.md) |
| SampleLocalService | [Consistent instance identity and bounded streaming](samples/SampleLocalService/PLAN.md) |
| SampleServiceDefaults | [Probe trace filters and registration readiness](samples/SampleServiceDefaults/PLAN.md) |
| Wafish.Regression | [Real-server streaming and reproducible fault schedules](tests/Wafish.Regression/PLAN.md) |
| Wafish.Administration.Regression | [HTTPS cookie flows and authorization matrices](tests/Wafish.Administration.Regression/PLAN.md) |
| Wafish.ReactUi.Host | [Deployment-prefix fixtures and controlled failure profiles](tests/Wafish.ReactUi.Host/PLAN.md) |
| Performance Backend | [Cancellation, bounded inputs, deterministic upstream faults](tests/Wafish.Performance/Backend/PLAN.md) |
| Performance NativeYarp | [Workload preflight and multiple-destination baselines](tests/Wafish.Performance/NativeYarp/PLAN.md) |
| Performance EnhancedYarp | [Effective policy validation and benchmark manifests](tests/Wafish.Performance/EnhancedYarp/PLAN.md) |
| Performance Runner | [Failure exit codes, body deadlines, scheduled arrival rates](tests/Wafish.Performance/Runner/PLAN.md) |

## Completion rule

Keep proposals TODO until implementation and acceptance checks pass.
Record the date, commands, evidence, and compatibility impact in the owning plan.
Update project documentation when implemented behavior changes.
Keep shared contracts and dependencies in this root review.
