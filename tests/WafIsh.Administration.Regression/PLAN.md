# WafIsh.Administration.Regression: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Split checks into focused modules

Status: TODO.

Extract authentication, configuration, backup, and MCP scenarios from Program.cs.

Acceptance: Scenario names and coverage remain intact after the split.

### IMP-02: Add explicit resource cleanup checks

Status: TODO.

Verify HTTP clients, websocket connections, temporary files, and hosts close after failed assertions.

Acceptance: A failed case does not prevent later cases from running.

## New valuable feature

### FEAT-01: Add session lifecycle state-machine tests

Status: TODO.

Exercise expiry, sign-out, two-factor changes, and concurrent requests across authentication modes.

Acceptance: Revoked or expired sessions cannot continue through APIs or realtime hubs.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
