# Runner: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Bound latency sample memory

Status: TODO.

Replace unbounded sample retention with a documented bounded measurement strategy.

Acceptance: Long soak runs retain accurate percentiles within a stated error bound.

### IMP-02: Record complete run metadata

Status: TODO.

Capture runtime, operating system, CPU, effective mode, and source revision when available.

Acceptance: Reports can be compared without guessing their execution conditions.

## New valuable feature

### FEAT-01: Add CI performance regression budgets

Status: TODO.

Compare a run against a selected baseline with explicit tolerances.

Acceptance: The runner reports regressions separately from request failures and measurement noise.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
