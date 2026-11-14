# WafIsh.Plugin.ServiceDiscovery: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

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

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
