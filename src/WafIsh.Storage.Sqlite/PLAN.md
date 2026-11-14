# WafIsh.Storage.Sqlite: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test interrupted migrations and recovery

Status: TODO.

Inject failures around migration commit, reopening, and WAL recovery.

Acceptance: Reopening either completes safely or reports a clear migration failure.

### IMP-02: Bound large log queries

Status: TODO.

Measure and limit allocations for large filters and pagination requests.

Acceptance: Stress checks keep memory and execution time within documented limits.

## New valuable feature

### FEAT-01: Add offline migration inspection

Status: TODO.

Provide a command that reports pending migrations without changing the database.

Acceptance: The report identifies schema state and pending steps while database bytes remain unchanged.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
