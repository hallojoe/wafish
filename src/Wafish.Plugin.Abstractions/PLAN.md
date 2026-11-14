# Wafish.Plugin.Abstractions: proposed work

Reviewed against source on 2026-11-14.
Statuses distinguish completed contract work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add contract compatibility checks

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [mutation and lifecycle contracts](../../tests/Wafish.Regression/evidence/mutation-contracts/README.md).
Release build, 200 main cases, 35 administration cases, and 23 browser cases pass.
Changed C# coverage is 87.36%. Three isolated package consumers and six compatibility guards pass.

Check public interface and record changes during package updates.

Acceptance: Unexpected binary or source breaks fail a dedicated compatibility check.

### IMP-02: Clarify lifecycle failure contracts

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [mutation and lifecycle contracts](../../tests/Wafish.Regression/evidence/mutation-contracts/README.md).
Release build, 200 main cases, 35 administration cases, and 23 browser cases pass.
Changed C# coverage is 87.36%. Three isolated package consumers and six compatibility guards pass.

Specify cancellation, repeat calls, and partial startup behavior for resources.

Acceptance: Reference implementations and tests demonstrate each lifecycle outcome.

## New valuable feature

### FEAT-01: Declare host capability requirements

Status: DONE. Completed: 2026-11-14.
Verification: Release build and `--plugin-policy` cases pass.
Checks cover cycles, missing targets, capability versions, callback order, and unchanged active domains.

Allow manifests to state required host capabilities and their versions.

Acceptance: The host rejects unsupported plugins before creating their resources.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Define statistic names and units

Status: DONE. Completed: 2026-11-14.
Verification: `--metric-contracts` verifies byte estimates, counters, durations, and unknown extension preservation. Priority: P2. Type: contract improvement.

Evidence: [IPluginRuntimeResource.cs](IPluginRuntimeResource.cs), `PluginRuntimeResourceSnapshot`.

Resource statistics use a free-form string-to-integer dictionary.
The contract does not identify units, counter resets, or measured versus estimated values.
Define standard descriptors while retaining namespaced extension statistics.

Acceptance: Consumers can distinguish byte estimates, cumulative counts, durations, and gauges.
Unknown extension statistics remain readable.

### FEAT-02: Declare pipeline ordering constraints

Status: DONE. Completed: 2026-11-14.
Verification: Release build and `--plugin-policy` cases pass.
Checks cover cycles, missing targets, capability versions, callback order, and unchanged active domains. Priority: P2. Type: feature.

Evidence: [PluginManifest.cs](PluginManifest.cs) supplies a numeric `Order` and dependency list.

Add explicit before/after requirements for plugins with ordering constraints.
Validate missing requirements and cycles before activating a domain.

Acceptance: Cache, compression, retry, and metrics retain their required ordering.
An invalid external plugin order is rejected while the active domain remains unchanged.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
