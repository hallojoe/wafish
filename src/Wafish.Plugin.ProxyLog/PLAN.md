# Wafish.Plugin.ProxyLog: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Bound decoded body allocations

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [review items 1–3 evidence](../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Review decompression paths that currently decode before applying display truncation.

Acceptance: Compressed test payloads cannot exceed the configured capture allocation budget.

### IMP-02: Test persistence faults during draining

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [lifetime stress evidence](../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).
Release build, 187 main cases, and 34 administration cases pass. Changed source coverage is 89.69%.

Inject storage failures while queued records remain at shutdown.

Acceptance: Counts, dropped records, and shutdown results accurately report the outcome.

## New valuable feature

### FEAT-01: Add per-route structured redaction rules

Status: DONE. Completed: 2026-11-14.
Verification: Release regression build and three `--log-safety` cases pass.
Route redactor executable-line coverage is 100%. See README for migration behavior.

Allow route bindings to specify JSON field paths and header redaction rules.

Acceptance: Sensitive fields are removed before persistence and realtime delivery.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### Evidence for IMP-01: Enforce limits before decompression

Priority: P1. Type: resource-exhaustion risk.

Evidence: [ProxyLogBodyReader.cs](Services/ProxyLogBodyReader.cs), `ReadRequestBodyAsync`, `ReadStreamAsync`, and `DecompressBytes`.

Request decoding uses `ReadToEndAsync` after checking compressed length.
Response decoding expands into an unrestricted stream before truncation.
Apply a decoded-byte limit during expansion and dispose decoder streams.

Acceptance: Small compressed payloads with large decoded content remain within the configured allocation budget.
Cancellation stops decoding promptly.

### IMP-03: Preserve and account for unfinished log batches

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: data-loss accounting defect.

Verification: [review items 8–10 evidence](../../tests/Wafish.Regression/evidence/review8910/README.md).
Release build and 157 regression executions pass. Changed production-line coverage is 86.67%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [AsyncLogPersistenceService.cs](Services/AsyncLogPersistenceService.cs), `ConsumeLoopAsync` and `FlushBatchAsync`.

Cancellation can interrupt a dequeued batch before persistence.
The final drain clears that batch before reading the remaining queue.
Exhausted write retries also omit failure and dropped-entry counter updates.
Retain unfinished entries until persistence or an explicit recorded discard.

Acceptance: Cancel after dequeue and before commit.
Each accepted entry is either persisted, pending, or counted as discarded.

### FEAT-02: Add log storage quotas

Status: TODO. Priority: P2. Type: feature.

Evidence: [SqliteProxyLogWriter.cs](Services/SqliteProxyLogWriter.cs), `CleanupAsync`, uses age-based retention.

Add configurable storage quotas for metadata and captured bodies.
Prefer dropping old bodies before removing their diagnostic metadata.
Expose quota pressure through resource health.

Acceptance: Sustained traffic respects the storage budget within a documented cleanup allowance.
Quota cleanup preserves recent metadata and reports discarded bodies.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
