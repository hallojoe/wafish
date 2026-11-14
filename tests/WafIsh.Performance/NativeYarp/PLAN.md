# NativeYarp: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Make baseline addresses configurable

Status: TODO.

Allow the runner to supply listener and upstream addresses.

Acceptance: The same route works on isolated port assignments.

### IMP-02: Record effective baseline configuration

Status: TODO.

Include proxy version and transport settings in benchmark metadata.

Acceptance: Reports identify the exact baseline configuration used.

## New valuable feature

### FEAT-01: Add selectable HTTP/2 and TLS baselines

Status: TODO.

Measure transport overhead with explicit, matched client and upstream settings.

Acceptance: Reports compare equivalent transport modes and document certificate setup.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
