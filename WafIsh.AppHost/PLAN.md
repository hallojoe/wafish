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

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../DOTNET_REVIEW.md).

### IMP-03: Isolate persistent resources across local topologies

Status: TODO. Priority: P2. Type: development isolation risk.

Evidence: [SonarQubeResources.cs](SonarQubeResources.cs) uses fixed named volumes.
[AppHost.cs](AppHost.cs) provides a configurable gateway data directory.

Add a documented topology identifier for optional container volumes and local state.
Preserve existing volume names through an explicit compatibility option.
Never delete existing volumes during startup.

Acceptance: Two isolated topologies cannot write each other's data.
Restarting one topology retains its own database and generated parameters.

### FEAT-02: Add a service-discovery demonstration profile

Status: TODO. Priority: P3. Type: feature.

Evidence: [AppHost.cs](AppHost.cs) currently demonstrates registration through gRPC.

Add an opt-in HTTP JSON discovery catalog for the sample backends.
Exercise endpoint addition, removal, stale discovery, and recovery through real plugin bindings.

Acceptance: The default topology remains unchanged.
The discovery profile demonstrates stable destination identity and visible refresh failures.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
