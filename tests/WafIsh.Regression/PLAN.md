# WafIsh.Regression: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Organize the runner around a shared case registry

Status: TODO.

Reduce scattered orchestration while preserving stable scenario names and exit behavior.

Acceptance: Existing checks still run once and failures remain individually identifiable.

### IMP-02: Expand deterministic concurrency coverage

Status: TODO.

Exercise snapshot publication, background audit loading, and plugin replacement with explicit barriers.

Acceptance: Repeated runs reproduce ordering failures without relying on machine speed.

## New valuable feature

### FEAT-01: Emit machine-readable test reports

Status: TODO.

Add a report format suitable for CI while retaining readable console output.

Acceptance: CI can identify every case, duration, and failure without parsing prose.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
