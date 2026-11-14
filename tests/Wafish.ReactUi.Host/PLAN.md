# Wafish.ReactUi.Host: proposed work

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

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Make hosting boundaries selectable

Status: IN PROGRESS. Priority: P2. Type: fixture coverage gap.

Path base and nested UI/API prefixes are implemented under review item 11.
JWT, BasicAuth, and API-key browser cases pass under `/edge`.
HTTPS fixture selection remains pending.
See [verification](../Wafish.Regression/evidence/review111213/README.md).

Original evidence: [Program.cs](Program.cs) fixed the administration prefix and exposed an HTTP loopback listener.

Add test-only options for a path base, custom prefixes, and HTTPS.
Keep each fixture's credentials, port, and storage isolated.

Acceptance: The same authentication cases run under prefixed and HTTPS hosting.
Asset and API requests remain inside the configured path base.

### FEAT-02: Add controlled API failure profiles

Status: TODO. Priority: P2. Type: test infrastructure feature.

Evidence: [Program.cs](Program.cs) seeds one working administration configuration.

Provide fixtures for storage failure, version conflict, delayed response, and activation failure.
Configure failures through the fixture process, without adding public fault-control endpoints.

Acceptance: Browser tests exercise real failure envelopes and revision fields.
A delayed failed mutation preserves the operator's draft.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
