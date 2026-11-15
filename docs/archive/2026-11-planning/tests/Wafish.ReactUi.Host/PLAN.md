# Wafish.ReactUi.Host: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Statuses distinguish completed work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test cleanup after interrupted runs

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [administration lifecycle evidence](../../../../../tests/Wafish.Regression/evidence/administration-lifecycle/README.md).

Check temporary state handling when browser startup or a host process fails.

Acceptance: Interrupted runs leave no reusable test credentials or locked database files.

### IMP-02: Make fixture setup reusable

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [regression infrastructure evidence](../../../../../tests/Wafish.Regression/evidence/regression-infrastructure/README.md).

Extract explicit fixture creation steps while preserving process isolation.

Acceptance: Authentication modes share setup behavior without sharing data.

## New valuable feature

### FEAT-01: Add selectable fixture profiles

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [regression infrastructure evidence](../../../../../tests/Wafish.Regression/evidence/regression-infrastructure/README.md).

Seed large route lists and representative plugin configurations through a test-only profile option.

Acceptance: Tests select a profile without editing source or contacting external services.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Make hosting boundaries selectable

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: fixture coverage gap.

Verification: [administration lifecycle evidence](../../../../../tests/Wafish.Regression/evidence/administration-lifecycle/README.md).

Path base and nested UI/API prefixes are implemented under review item 11.
JWT, BasicAuth, and API-key browser cases pass under `/edge`.
HTTPS fixture selection is now verified across all three browser authentication modes.
See [verification](../../../../../tests/Wafish.Regression/evidence/review111213/README.md).

Original evidence: [Program.cs](../../../../../tests/Wafish.ReactUi.Host/Program.cs) fixed the administration prefix and exposed an HTTP loopback listener.

Add test-only options for a path base, custom prefixes, and HTTPS.
Keep each fixture's credentials, port, and storage isolated.

Acceptance: The same authentication cases run under prefixed and HTTPS hosting.
Asset and API requests remain inside the configured path base.

### FEAT-02: Add controlled API failure profiles

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: test infrastructure feature.

Verification: [regression infrastructure evidence](../../../../../tests/Wafish.Regression/evidence/regression-infrastructure/README.md).

Evidence: [Program.cs](../../../../../tests/Wafish.ReactUi.Host/Program.cs) seeds one working administration configuration.

Provide fixtures for storage failure, version conflict, delayed response, and activation failure.
Configure failures through the fixture process, without adding public fault-control endpoints.

Acceptance: Browser tests exercise real failure envelopes and revision fields.
A delayed failed mutation preserves the operator's draft.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../tests/Wafish.ReactUi.Host/README.md) and [AGENTS.md](../../../../../tests/Wafish.ReactUi.Host/AGENTS.md) when the implemented behavior changes.
