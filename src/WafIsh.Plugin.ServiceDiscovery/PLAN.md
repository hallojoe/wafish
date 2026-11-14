# WafIsh.Plugin.ServiceDiscovery: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add provider response contract fixtures

Status: TODO.

Cover empty sets, malformed JSON, unhealthy instances, and cancellation for every provider.

Acceptance: Each parser has repeatable success and failure cases.

### IMP-02: Control refresh backoff and stale visibility

Status: TODO.

Expose refresh failures and stale age, and distribute retry timing.

Acceptance: Operators can distinguish healthy empty discovery from repeated provider failure.

## New valuable feature

### FEAT-01: Support Kubernetes EndpointSlice discovery

Status: TODO.

Resolve ready endpoints from EndpointSlice resources with explicit access requirements.

Acceptance: Tests cover multiple slices, readiness changes, and endpoint removal.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
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

Status: TODO. Priority: P2. Type: health-reporting defect.

Evidence: [ServiceDiscoveryRefreshService.cs](ServiceDiscoveryRefreshService.cs), `RunAsync` and `RefreshDueBindingsAsync`.

Binding failures set `_lastError` but are caught inside the refresh method.
The outer loop then clears `_lastError` unconditionally.
Track health per binding and clear errors only after its successful refresh.

Acceptance: One failing provider keeps aggregate health degraded while other providers succeed.
A later successful refresh clears only the recovered binding.

### FEAT-02: Configure maximum destination staleness

Status: TODO. Priority: P2. Type: feature.

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

Completed IMP-03 on 2026-11-14. [Verification](../../tests/WafIsh.Regression/evidence/review4567/README.md).
