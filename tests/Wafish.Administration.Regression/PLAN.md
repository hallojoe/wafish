# Wafish.Administration.Regression: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Split checks into focused modules

Status: TODO.

Extract authentication, configuration, backup, and MCP scenarios from Program.cs.

Acceptance: Scenario names and coverage remain intact after the split.

### IMP-02: Add explicit resource cleanup checks

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [administration lifecycle evidence](../Wafish.Regression/evidence/administration-lifecycle/README.md).

Verify HTTP clients, websocket connections, temporary files, and hosts close after failed assertions.

Acceptance: A failed case does not prevent later cases from running.

## New valuable feature

### FEAT-01: Add session lifecycle state-machine tests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [administration lifecycle evidence](../Wafish.Regression/evidence/administration-lifecycle/README.md).

Exercise expiry, sign-out, two-factor changes, and concurrent requests across authentication modes.

Acceptance: Revoked or expired sessions cannot continue through APIs or realtime hubs.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Verify secure-cookie transport behavior

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: coverage gap.

Verification: [review items 1–3 evidence](../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [Program.cs](Program.cs), `Client`, `StartHost`, and the JWT login check.

The host uses HTTP loopback.
The client disables cookies and authenticates with a bearer token after inspecting cookie attributes.
Add HTTPS cases with a real cookie container and controlled test certificates.
Retain the existing token checks.

Acceptance: Login, logout, expiry, and origin checks run through cookie transport.
Cookies are not sent to an unintended host or transport.

### FEAT-02: Run the administration authorization matrix

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: test infrastructure feature.

Verification: [administration lifecycle evidence](../Wafish.Regression/evidence/administration-lifecycle/README.md).

Evidence: [Program.cs](Program.cs) checks selected routes and hub negotiations.
A [route inventory](../Wafish.Regression/contracts/dashboard-routes.json) already exists.

Generate anonymous, invalid-credential, and authorized checks from the controller inventory.
Add explicit exceptions for authentication entry points.
Extend the matrix when operation permissions are introduced.

Acceptance: Every mapped protected action has an authorization case.
Unexpected public routes fail verification.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
