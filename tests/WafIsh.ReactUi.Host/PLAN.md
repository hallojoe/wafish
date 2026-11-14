# WafIsh.ReactUi.Host: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test cleanup after interrupted runs

Status: TODO.

Check temporary state handling when browser startup or a host process fails.

Acceptance: Interrupted runs leave no reusable test credentials or locked database files.

### IMP-02: Make fixture setup reusable

Status: TODO.

Extract explicit fixture creation steps while preserving process isolation.

Acceptance: Authentication modes share setup behavior without sharing data.

## New valuable feature

### FEAT-01: Add selectable fixture profiles

Status: TODO.

Seed large route lists and representative plugin configurations through a test-only profile option.

Acceptance: Tests select a profile without editing source or contacting external services.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
