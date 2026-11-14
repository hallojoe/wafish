# WafIsh.Plugin.Metrics: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test concurrent snapshots and updates

Status: TODO.

Exercise metric reads while requests update counters and latency values.

Acceptance: Snapshots remain internally valid and counters do not lose updates.

### IMP-02: Define metric units and reset semantics

Status: TODO.

Document bytes, duration units, errors, and process restart behavior.

Acceptance: Tests verify the documented units and reset behavior.

## New valuable feature

### FEAT-01: Export plugin measurements through OpenTelemetry

Status: TODO.

Bridge these measurements to standard instruments with controlled dimensions.

Acceptance: A test collector receives route and cluster metrics without duplicate request counts.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
