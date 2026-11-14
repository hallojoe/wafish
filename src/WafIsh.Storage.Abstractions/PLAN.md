# WafIsh.Storage.Abstractions: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Create provider conformance tests

Status: TODO.

Exercise rollback, ordering, cancellation, null handling, and identity preservation through the interfaces.

Acceptance: Every provider can run the same contract suite.

### IMP-02: Define pagination guarantees

Status: TODO.

Specify stable ordering and limits for repository methods returning history or logs.

Acceptance: Paging tests show no duplicate or skipped rows for a stable dataset.

## New valuable feature

### FEAT-01: Expose provider capability metadata

Status: TODO.

Describe provider support for encryption, backup, and transaction features.

Acceptance: Hosts can reject unsupported options before accepting traffic.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
