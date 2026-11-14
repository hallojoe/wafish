# WafIsh.Plugin.CircuitBreaker: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Introduce controllable time for recovery tests

Status: TODO.

Replace wall-clock assumptions with an injectable time source where needed.

Acceptance: Open and half-open transitions can be tested without real delays.

### IMP-02: Stress concurrent half-open requests

Status: TODO.

Run competing probes against one destination and simulate cancellation.

Acceptance: The configured probe limit is never exceeded.

## New valuable feature

### FEAT-01: Add configurable recovery jitter

Status: TODO.

Spread recovery probes across replicas after a shared outage.

Acceptance: Probe times vary within configured bounds without extending outages indefinitely.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
