# Wafish.Plugin.Metrics: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test concurrent snapshots and updates

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [generated protocol evidence](../../tests/Wafish.Regression/evidence/protocol-properties/README.md).

Exercise metric reads while requests update counters and latency values.

Acceptance: Snapshots remain internally valid and counters do not lose updates.

### IMP-02: Define metric units and reset semantics

Status: DONE. Completed: 2026-11-14.
Verification: `--metric-contracts` and `--protocol-properties` pass.
A MeterListener receives route and cluster counters, with explicit units and no duplicated attempts.
Concurrent snapshot checks verify retirement and reset behavior.

Document bytes, duration units, errors, and process restart behavior.

Acceptance: Tests verify the documented units and reset behavior.

## New valuable feature

### FEAT-01: Export plugin measurements through OpenTelemetry

Status: DONE. Completed: 2026-11-14.
Verification: `--metric-contracts` and `--protocol-properties` pass.
A MeterListener receives route and cluster counters, with explicit units and no duplicated attempts.
Concurrent snapshot checks verify retirement and reset behavior.

Bridge these measurements to standard instruments with controlled dimensions.

Acceptance: A test collector receives route and cluster metrics without duplicate request counts.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include implementation and acceptance evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Record failed and cancelled requests

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: measurement gap.

Verification: [review items 8–10 evidence](../../tests/Wafish.Regression/evidence/review8910/README.md).
Release build and 157 regression executions pass. Changed production-line coverage is 86.67%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [PluginMetricsMiddleware.cs](PluginMetricsMiddleware.cs), `InvokeAsync`.

Measurements occur only after the downstream delegate returns successfully.
Exceptions and cancellation skip accounting.
Record completion in a finalization path with distinct outcome classification.
Also define whether measurements count logical requests or retry attempts.

Acceptance: Throwing, cancelled, and successful delegates each produce one documented outcome.
Enabling both metric plugins does not duplicate counts.

### IMP-04: Remove counters for retired identities

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: retention risk.

Verification: [review items 14–16](../../tests/Wafish.Regression/evidence/review141516/README.md).
Concurrent updates retain active counters. Removed identities and the series budget are covered.

Evidence: [PluginMetricsMiddleware.cs](PluginMetricsMiddleware.cs), `PluginMetricStore`.

Route, cluster, and destination dictionaries only add entries.
Repeated configuration churn retains obsolete identities for the store lifetime.
Apply bounded retention when identities leave active snapshots.

Acceptance: Repeated destination replacement reaches a stable memory bound.
Concurrent updates and removals preserve active counters.

### FEAT-02: Add route latency distributions

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [R19–R28 evidence](../../tests/Wafish.Regression/evidence/need-seven/README.md).
Compatibility and limits: [feature contracts](../../docs/need-seven-features.md).

Evidence: `PluginMetricSnapshot` stores total duration but no route latency distribution.

Add bounded duration histograms for routes and clusters.
Use explicit units and documented bucket boundaries.
Connect them to FEAT-01 telemetry exports.

Acceptance: Known latency samples enter the expected buckets.
Operators can inspect tail latency without retaining every request duration.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
