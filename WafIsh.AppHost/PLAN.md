# WafIsh.AppHost: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add topology restart checks

Status: TODO.

Verify persisted configuration, credentials, and backend registration after controlled restarts.

Acceptance: Restart checks retain state and recover both backend instances.

### IMP-02: Add permanent SonarQube health tests

Status: TODO.

Exercise startup, UP, malformed, timeout, and unavailable status responses.

Acceptance: Only an UP status passes the readiness check.

## New valuable feature

### FEAT-01: Add optional network fault injection

Status: TODO.

Provide a local resource that can delay or interrupt backend traffic on request.

Acceptance: A repeatable scenario demonstrates retries and circuit recovery without changing production defaults.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
