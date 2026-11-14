# WafIsh.Plugin.RateLimit.Redis: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test Redis failover and reconnection

Status: TODO.

Extend live tests to cover connection replacement and interrupted Lua operations.

Acceptance: Recovery preserves outage policy and shared quota behavior.

### IMP-02: Validate key isolation across deployments

Status: TODO.

Test configured prefixes with identical routes and client addresses.

Acceptance: One deployment cannot consume another deployment's quota accidentally.

## New valuable feature

### FEAT-01: Add trusted tenant quota partitions

Status: TODO.

Allow an explicitly configured, authenticated tenant claim to select the shared quota key.

Acceptance: Missing or untrusted claims follow a documented policy and cannot create unlimited key cardinality.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
