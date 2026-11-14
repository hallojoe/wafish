# WafIsh: proposed work

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

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
