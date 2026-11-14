# EnhancedYarp: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Validate mode fidelity at startup

Status: TODO.

Check that every advertised feature mode creates the intended effective bindings and resources.

Acceptance: Each mode reports its effective configuration and fails if required features are inactive.

### IMP-02: Remove remaining legacy labels and configuration keys

Status: TODO.

Review scenario names and options against current plugin manifests.

Acceptance: The harness uses accepted schema fields and clearly named comparison groups.

## New valuable feature

### FEAT-01: Add a bounded plugin-combination matrix

Status: TODO.

Generate selected plugin combinations with a recorded configuration for each run.

Acceptance: Reports attribute cost to explicit combinations and keep the matrix size controlled.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
