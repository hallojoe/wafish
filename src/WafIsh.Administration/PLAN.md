# WafIsh.Administration: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Unify mutation error contracts

Status: TODO.

Define shared response models for validation, version, storage, and activation failures.

Acceptance: Clients receive consistent status and revision fields across mutation endpoints.

### IMP-02: Expand shutdown and notification tests

Status: TODO.

Exercise queued audit notifications and realtime clients during host shutdown.

Acceptance: Shutdown completes without exposing credentials or reporting uncommitted changes.

## New valuable feature

### FEAT-01: Add WebAuthn authentication

Status: TODO.

Support registered authenticators as an additional administration login method.

Acceptance: Enrollment, challenge expiry, revocation, and recovery have integration tests.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
