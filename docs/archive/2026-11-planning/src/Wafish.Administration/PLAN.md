# Wafish.Administration: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Unify mutation error contracts

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [mutation and lifecycle contracts](../../../../../tests/Wafish.Regression/evidence/mutation-contracts/README.md).
Release build, 200 main cases, 35 administration cases, and 23 browser cases pass.
Changed C# coverage is 87.36%. Three isolated package consumers and six compatibility guards pass.

Define shared response models for validation, version, storage, and activation failures.

Acceptance: Clients receive consistent status and revision fields across mutation endpoints.

### IMP-02: Expand shutdown and notification tests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [lifetime stress evidence](../../../../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).
Release build, 187 main cases, and 34 administration cases pass. Changed source coverage is 89.69%.

Exercise queued audit notifications and realtime clients during host shutdown.

Acceptance: Shutdown completes without exposing credentials or reporting uncommitted changes.

## New valuable feature

### FEAT-01: Add WebAuthn authentication

Status: TODO.

Support registered authenticators as an additional administration login method.

Acceptance: Enrollment, challenge expiry, revocation, and recovery have integration tests.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Limit authentication attempts

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: protection gap.

Verification: [review items 1–3 evidence](../../../../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [DashboardAuthController.cs](../../../../../src/Wafish.Administration/Modules/Dashboard/Controllers/DashboardAuthController.cs), `Login`.
See [DashboardAuthorizationService.cs](../../../../../src/Wafish.Administration/Infrastructure/Auth/DashboardAuthorizationService.cs) for Basic and API-key checks.

These paths do not apply an authentication attempt limit.
Route plugins do not provide protection for administration login.
Add explicit limits for password, TOTP, and direct credential attempts.
Use trusted connection identity and bounded account partitions.

Acceptance: Repeated failures receive HTTP 429 with retry guidance.
Healthy users remain usable, and limits work without proxy plugins.

### IMP-04: Count notification queue losses

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: observability defect.

Verification: [review items 8–10 evidence](../../../../../tests/Wafish.Regression/evidence/review8910/README.md).
Release build and 157 regression executions pass. Changed production-line coverage is 86.67%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [NotificationDispatcher.cs](../../../../../src/Wafish.Administration/Infrastructure/Notifications/NotificationDispatcher.cs), queue construction and `Enqueue`.

The channel uses `DropOldest`, but loss reporting depends on `TryWrite` returning false.
An accepted write can discard an older event.
Count discarded events explicitly and report shutdown leftovers.

Acceptance: Overfilling the queue reports the exact discarded count.
Health distinguishes queued, delivered, failed, and discarded notifications.

### FEAT-02: Add operation permissions

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: feature.

Verification: [R15–R18 evidence](../../../../../tests/Wafish.Regression/evidence/operation-previews/README.md).
Release build, 252 main cases, 40 administration cases, and 30 browser checks pass.
Changed production-line coverage is 98.63%.

Evidence: [DashboardAuthorizationService.cs](../../../../../src/Wafish.Administration/Infrastructure/Auth/DashboardAuthorizationService.cs) returns one authorization decision.

Introduce read, configuration-write, plugin-management, and credential-management permissions.
Apply identical permissions to controllers, realtime subscriptions, and optional management tools.
Preserve supported authentication modes and custom authorization integration.

Acceptance: A reader can inspect routes but cannot mutate configuration.
Audit records identify the actor and denied operation.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../src/Wafish.Administration/README.md) and [AGENTS.md](../../../../../src/Wafish.Administration/AGENTS.md) when the implemented behavior changes.
