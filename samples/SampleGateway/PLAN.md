# SampleGateway: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add a container smoke check

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample deployment](../../tests/Wafish.Regression/evidence/sample-deployment/README.md).

Start the published image with temporary storage and injected credentials.

Acceptance: The non-root container serves readiness and authenticated administration endpoints.

### IMP-02: Add a reproducible local HTTPS recipe

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample deployment](../../tests/Wafish.Regression/evidence/sample-deployment/README.md).

Document certificates, trusted proxies, and secure-cookie behavior for a standalone host.

Acceptance: The documented setup passes login and proxy smoke checks.

## New valuable feature

### FEAT-01: Add a blue-green deployment example

Status: TODO.

Demonstrate a health-checked switch between two backend versions using existing configuration APIs.

Acceptance: The walkthrough proves cutover and rollback without losing the original route identity.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Build operator URLs from effective configuration

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample deployment](../../tests/Wafish.Regression/evidence/sample-deployment/README.md). Priority: P2. Type: sample mismatch.

Evidence: [Program.cs](Program.cs), startup output and browser launch.

The sample prints `/console` and builds an HTTP browser URL.
UI prefixes and Kestrel transport settings are configurable.
Use the resolved scheme, authority, and configured UI prefix.

Acceptance: A custom UI prefix and HTTPS listener produce a valid browser link.
Orchestrated startup still respects the disabled automatic-browser setting.

### FEAT-02: Provide a minimal runtime composition profile

Status: TODO. Priority: P3. Type: sample feature.

Evidence: [Program.cs](Program.cs) always registers React administration and AI services.

Add a runnable profile that selects runtime and storage without administration registration.
Document the separate full-host profile using the public composition APIs.

Acceptance: The minimal profile forwards traffic and restores saved configuration.
Administration, AI, and MCP endpoints are absent from that profile.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
