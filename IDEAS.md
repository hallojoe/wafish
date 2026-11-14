# WafIsh deferred ideas

Created: 2026-11-13. Last updated: 2026-11-13.

This file records proposals outside the active [execution plan](PLAN.md).
Ideas are not implementation commitments. Move approved work into PLAN.md with task identifiers and acceptance checks.

## IDEA-01 Separate administration and gateway processes

Status: DEFERRED. Related task: [ARCH-01](PLAN.md#arch-01-define-package-ownership).

### Purpose

Run administration independently from gateway traffic processing.
This could support central management, independent scaling, and separate network access policies.

### Current decision

Phase one separates packages while retaining runtime and administration within one host.
It makes the UI optional. It does not introduce a remote gateway-management protocol.
Preserve clean interfaces so process separation remains possible later.

### Required design work

- Define gateway identity, enrollment, credential rotation, and revocation.
- Select an authenticated transport and authorize each management operation.
- Define configuration ownership, versioning, delivery, acknowledgment, and conflict handling.
- Distinguish persisted configuration from configuration active on each gateway.
- Define behavior during disconnection, partial rollout, and administration outages.
- Plan secure telemetry and audit attribution across process boundaries.
- Define topology, discovery, network exposure, health checks, and deployment requirements.
- Specify storage ownership and migration from the current single-host model.
- Version the management protocol and define compatibility between independently deployed components.

### Promotion criteria

- A concrete requirement needs independent administration deployment or management of multiple gateways.
- Package separation and configuration persistence checks are complete.
- The user selects the operational model and accepts its additional complexity.
- Failure behavior, security boundaries, migration steps, and acceptance checks are documented.

### Future acceptance scenarios

- An enrolled gateway receives and acknowledges an authorized configuration update.
- An unauthorized or revoked gateway cannot access management operations.
- A disconnected gateway follows an explicit last-known-configuration policy.
- Partial application across gateways is visible and recoverable.
- Every update identifies its actor, target gateway, persisted version, and active version.
- Administration restart does not interrupt gateway request processing.
