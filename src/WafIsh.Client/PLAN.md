# WafIsh.Client: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add startup retry jitter

Status: TODO.

Distribute registration retries when many services restart together.

Acceptance: Tests use controlled delays and verify cancellation during retry waits.

### IMP-02: Test heartbeats during shutdown

Status: TODO.

Exercise an in-flight heartbeat when unregistration starts.

Acceptance: Shutdown cannot register the instance again after successful unregistration.

## New valuable feature

### FEAT-01: Support ordered gateway failover

Status: TODO.

Allow a configured list of registration endpoints with clear ownership rules.

Acceptance: Failover preserves instance identity and does not create conflicting registrations.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
