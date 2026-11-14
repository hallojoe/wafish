# Wafish.Regression: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Organize the runner around a shared case registry

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [regression infrastructure evidence](evidence/regression-infrastructure/README.md).

Reduce scattered orchestration while preserving stable scenario names and exit behavior.

Acceptance: Existing checks still run once and failures remain individually identifiable.

### IMP-02: Expand deterministic concurrency coverage

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [regression infrastructure evidence](evidence/regression-infrastructure/README.md).

Exercise snapshot publication, background audit loading, and plugin replacement with explicit barriers.

Acceptance: Repeated runs reproduce ordering failures without relying on machine speed.

## New valuable feature

### FEAT-01: Emit machine-readable test reports

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [regression infrastructure evidence](evidence/regression-infrastructure/README.md).

Add a report format suitable for CI while retaining readable console output.

Acceptance: CI can identify every case, duration, and failure without parsing prose.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Add real-server streaming regressions

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: coverage gap.

Verification: [review items 1–3 evidence](../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [ResponseStreamingChecks.cs](ResponseStreamingChecks.cs) uses `DefaultHttpContext` and memory streams.

These checks verify captured bytes but do not reproduce Kestrel response-start behavior.
Add isolated server cases for retry, compression, cache, and early client disconnect.
Keep existing focused middleware checks.

Acceptance: Tests detect header changes after response start and truncated replay.
Large streaming responses remain bounded and deliver an early first byte.

### FEAT-02: Add reproducible fault schedules

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: test infrastructure feature.

Verification: [regression infrastructure evidence](evidence/regression-infrastructure/README.md).

Evidence: [ConfigurationTransactionChecks.cs](ConfigurationTransactionChecks.cs) already uses blocking storage and a failing publisher.

Generalize these hooks into named fault schedules.
Record the seed and operation sequence for publication, persistence, and shutdown failures.

Acceptance: A reported failure can be replayed without timing assumptions.
Schedules include solution NET-IMP-01 and the retry and log-drain findings.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
