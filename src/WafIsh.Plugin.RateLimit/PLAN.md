# WafIsh.Plugin.RateLimit: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

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

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
