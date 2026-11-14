# Wafish.Plugin.ServiceDiscovery: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add provider response contract fixtures

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Cover empty sets, malformed JSON, unhealthy instances, and cancellation for every provider.

Acceptance: Each parser has repeatable success and failure cases.

### IMP-02: Control refresh backoff and stale visibility

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Expose refresh failures and stale age, and distribute retry timing.

Acceptance: Operators can distinguish healthy empty discovery from repeated provider failure.

## New valuable feature

### FEAT-01: Support Kubernetes EndpointSlice discovery

Status: DONE. Owner: Codex. Completed: 2026-11-15.

Verification: Release regression build and `--group need-six`; paginated IPv4/IPv6, readiness, removal, bounds, and failure checks pass.
See [EndpointSlice contracts](../../docs/need-six-features.md#kubernetes-endpointslice-discovery).

Resolve ready endpoints from EndpointSlice resources with explicit access requirements.

Acceptance: Tests cover multiple slices, readiness changes, and endpoint removal.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items have verification evidence below.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Keep destination identities stable

Status: DONE. Priority: P1. Type: identity risk.

Evidence: [ServiceDiscoveryRefreshService.cs](ServiceDiscoveryRefreshService.cs), `PublishDestinationsAsync`.

Destination identifiers use sorted list positions such as `discovery-0000`.
Adding an earlier address shifts identifiers for existing destinations.
Circuit and health state can then refer to a different endpoint.
Derive identity from a stable provider identifier or normalized address.

Acceptance: Inserting, removing, and reordering endpoints preserves every unchanged destination identity.
New endpoints do not inherit another endpoint's circuit state.

### IMP-04: Preserve discovery failure health

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: health-reporting defect.

Evidence: [ServiceDiscoveryRefreshService.cs](ServiceDiscoveryRefreshService.cs), `RunAsync` and `RefreshDueBindingsAsync`.

Binding failures set `_lastError` but are caught inside the refresh method.
The outer loop then clears `_lastError` unconditionally.
Track health per binding and clear errors only after its successful refresh.

Acceptance: One failing provider keeps aggregate health degraded while other providers succeed.
A later successful refresh clears only the recovered binding.

### FEAT-02: Configure maximum destination staleness

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: feature.

Evidence: [ServiceDiscoveryRefreshService.cs](ServiceDiscoveryRefreshService.cs) retains previous destinations after refresh failures.

Expose last-success time and a configurable maximum stale age.
Define explicit retain-last-known and reject-new-traffic policies.
Distinguish a valid empty provider result from provider failure.

Acceptance: Clock-controlled tests verify stale expiry and recovery.
Snapshot publication follows solution item NET-IMP-01.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../tests/Wafish.Regression/evidence/review4567/README.md).

Completed IMP-01, IMP-02, IMP-04, FEAT-02 on 2026-11-14. [Verification](../../tests/Wafish.Regression/evidence/recovery/README.md).
