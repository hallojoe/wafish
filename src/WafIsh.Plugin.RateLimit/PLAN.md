# WafIsh.Plugin.RateLimit: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Expand algorithm boundary tests

Status: TODO.

Cover replenishment, queue cancellation, and concurrent acquisition for each algorithm.

Acceptance: Permit counts and rejection headers remain correct at boundaries.

### IMP-02: Test partition cleanup under churn

Status: TODO.

Generate many client partitions and exercise stale limiter cleanup.

Acceptance: Memory remains bounded and active partitions retain valid limits.

## New valuable feature

### FEAT-01: Support named quota groups

Status: TODO.

Let selected routes share an explicitly named local quota.

Acceptance: Grouped routes share permits while unrelated routes remain isolated.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Enforce partition limits during admission

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: resource-bound gap.

Verification: [review items 1–3 evidence](../../tests/WafIsh.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [RateLimitMiddleware.cs](Middleware/RateLimitMiddleware.cs), `TryCleanup` and `GetPartitionValue`.
See [runtime store](../WafIsh.Runtime/Infrastructure/State/InMemoryRateLimiterStore.cs).

The 2,000-entry threshold is checked only during five-minute cleanup.
New partitions can exceed it between cleanup passes.
The Route partition uses raw paths, so variable paths create separate counters.
Define stable route partitions and a bounded overflow policy.

Acceptance: Many distinct paths cannot create unlimited limiters.
Existing active limits remain enforced during cleanup.
Coordinate ownership with Runtime IMP-03.

### FEAT-02: Support weighted request costs

Status: TODO. Priority: P3. Type: feature.

Evidence: [RateLimitMiddleware.cs](Middleware/RateLimitMiddleware.cs) always acquires one permit.

Allow validated route policy to assign a request cost for token and window algorithms.
Take costs from trusted configuration rather than caller headers.

Acceptance: An expensive route consumes its configured allowance.
Invalid costs fail validation, and rejection reports usable retry information.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
