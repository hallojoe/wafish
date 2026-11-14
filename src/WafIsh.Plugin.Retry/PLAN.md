# WafIsh.Plugin.Retry: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test replay and response boundaries

Status: TODO.

Cover partial responses, streamed bodies, cancellation, and exhausted destinations.

Acceptance: Unsafe requests are not replayed and no response bytes are duplicated.

### IMP-02: Make timing tests repeatable

Status: TODO.

Use controlled backoff and jitter sources in focused tests.

Acceptance: Retry timing and total attempt limits can be asserted without long sleeps.

## New valuable feature

### FEAT-01: Add a cluster-wide retry budget

Status: TODO.

Limit extra attempts during a widespread upstream outage.

Acceptance: Retries stop at the configured shared budget while initial requests retain their normal policy.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
