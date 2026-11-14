# Wafish: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand concurrent mutation tests

Status: TODO.

Use generated operation sequences to test rename, deletion, registration, and stale versions.

Acceptance: Repeated sequences preserve identities and reject stale updates without partial writes.

### IMP-02: Add cancellation checkpoints to configuration loading

Status: TODO.

Review startup and mutation paths that currently use default cancellation tokens.

Acceptance: Cancellation leaves persisted and active state consistent.

## New valuable feature

### FEAT-01: Add mutation idempotency keys

Status: TODO.

Allow callers to repeat a configuration request after a connection failure.

Acceptance: The same key and payload return the original result without creating another revision.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Remove orphaned clusters after the complete batch

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: defect.

Verification: [review items 8–10 evidence](../../tests/Wafish.Regression/evidence/review8910/README.md).
Release build and 157 regression executions pass. Changed production-line coverage is 86.67%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [DynamicYarpConfigService.cs](Services/DynamicYarpConfigService.cs), `BatchDeleteRoutesAsync`.

The method accumulates surviving cluster references after each deletion.
Deleting two routes for one cluster can retain that cluster in `survivors`.
Calculate surviving references after all requested deletions.

Acceptance: Deleting both routes removes their cluster when orphan cleanup is enabled.
A cluster with a remaining route stays unchanged.

### IMP-04: Make configuration preparation asynchronous

Status: TODO. Priority: P2. Type: improvement.

Evidence: [DynamicConfigPublisher.cs](Services/DynamicConfigPublisher.cs), `Prepare` and `PreValidate`.

Preparation blocks on asynchronous compilation and YARP validation while holding the mutation lock.
Introduce asynchronous preparation and pass cancellation through precommit work.
Extend IMP-02 without treating cancellation after persistence as a completed rollback.

Acceptance: A delayed validator does not block a worker thread.
Cancellation before persistence leaves both revisions unchanged.

### FEAT-02: Preview a configuration change

Status: TODO. Priority: P2. Type: feature.

Evidence: [ConfigurationMutation.cs](Services/ConfigurationMutation.cs) already separates preparation from persistence.

Expose validation results, normalized changes, and the expected revision before applying a candidate.
Return a preview without storing configuration or publishing a snapshot.

Acceptance: Invalid references produce actionable errors.
Previewing a valid change leaves storage, active routes, and plugin resources unchanged.
Depends on solution item NET-IMP-01.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
