# Wafish.Plugin.Cache: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand cache-header property tests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [generated protocol evidence](../../tests/Wafish.Regression/evidence/protocol-properties/README.md).

Generate combinations of freshness, Vary, Age, and request cache directives.

Acceptance: Generated cases preserve isolation and freshness requirements.

### IMP-02: Measure concurrent capture pressure

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [allocation pressure](../../tests/Wafish.Regression/evidence/allocation-pressure/README.md).

Benchmark simultaneous misses and eviction while clients read cached entries.

Acceptance: The configured budget bounds retained memory under concurrent load.

## New valuable feature

### FEAT-01: Add route-scoped cache tags and purge

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [R19–R28 evidence](../../tests/Wafish.Regression/evidence/need-seven/README.md).
Compatibility and limits: [feature contracts](../../docs/need-seven-features.md).

Allow authenticated administration to invalidate tagged public responses.

Acceptance: Purging one route or tag leaves unrelated entries available.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include implementation and acceptance evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Reconcile cache settings with effective keys

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: configuration mismatch.

Verification: [review items 11–13 evidence](../../tests/Wafish.Regression/evidence/review111213/README.md).
Release build, 163 regression executions, and 20 browser checks pass.
Changed C# source coverage is 93.85%. Asset-validator coverage is 100%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [ResponseCachePlugin.cs](ResponseCachePlugin.cs) and [ResponseCacheMiddleware.cs](ResponseCacheMiddleware.cs).

The schema exposes `varyByQuery` and `varyHeaders`.
The middleware always includes the query and all nonexcluded request headers.
Document this conservative behavior and deprecate ineffective switches, or define a safe supported meaning.

Acceptance: Each exposed setting has a tested effect or a clear deprecation message.
Host, query, credentials, and response variants remain isolated.

### IMP-04: Reduce retained allocation for small responses

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [allocation pressure](../../tests/Wafish.Regression/evidence/allocation-pressure/README.md). Priority: P2. Type: performance hypothesis.

Evidence: [ResponseCacheStore.cs](ResponseCacheStore.cs), `Reserve` and `Store`.

Each entry retains its full capture buffer, even when the response is small.
The default route limit reserves one MiB plus header allowance per capture.
Measure segmented capture or resizing after completion.

Acceptance: Small responses retain substantially less memory.
Peak accounting includes resizing, active readers, and pending captures.

### FEAT-02: Share concurrent cache fills

Status: TODO. Priority: P2. Type: feature.

Evidence: [ResponseCacheMiddleware.cs](ResponseCacheMiddleware.cs) forwards every simultaneous cache miss.

Add bounded coordination for identical public cache keys.
Waiters must cancel independently and respect a maximum wait.

Acceptance: Concurrent eligible misses avoid repeated upstream work.
Private responses, failed fills, and generation changes never expose an invalid shared response.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
