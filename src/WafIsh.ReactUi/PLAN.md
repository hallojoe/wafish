# WafIsh.ReactUi: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand page-level component coverage

Status: TODO.

Cover draft preservation, stale requests, and loading-to-error transitions for each workspace.

Acceptance: Each workspace has tests beyond the shared API client.

### IMP-02: Check accessibility across dynamic states

Status: TODO.

Test dialogs, validation errors, realtime disconnects, and mobile navigation.

Acceptance: Keyboard and automated accessibility checks pass for these states.

## New valuable feature

### FEAT-01: Add saved operator views

Status: TODO.

Persist named filters and column choices through an authenticated per-user API.

Acceptance: Views restore after sign-in without storing credentials or sharing private preferences between users.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
