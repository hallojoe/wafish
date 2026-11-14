# WafIsh.Storage.Abstractions: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Create provider conformance tests

Status: TODO.

Exercise rollback, ordering, cancellation, null handling, and identity preservation through the interfaces.

Acceptance: Every provider can run the same contract suite.

### IMP-02: Define pagination guarantees

Status: TODO.

Specify stable ordering and limits for repository methods returning history or logs.

Acceptance: Paging tests show no duplicate or skipped rows for a stable dataset.

## New valuable feature

### FEAT-01: Expose provider capability metadata

Status: TODO.

Describe provider support for encryption, backup, and transaction features.

Acceptance: Hosts can reject unsupported options before accepting traffic.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Add an atomic binding batch contract

Status: DONE. Priority: P1. Type: consistency gap.

Evidence: [IPluginConfigurationRepository.cs](Storage/IPluginConfigurationRepository.cs).
Compare [IConfigurationSnapshotRepository.cs](IConfigurationSnapshotRepository.cs).

Configuration snapshots have expected-version checks.
Plugin bindings expose individual writes without a batch revision.
Specify atomic binding updates and deletes with an expected revision.
Define rollback and concurrent-reader guarantees for every provider.

Acceptance: Failure halfway through a batch exposes no partial state.
Two writers cannot silently overwrite the same binding revision.
Coordinate with solution item NET-IMP-01.

### FEAT-02: Add a storage backup capability

Status: TODO. Priority: P2. Type: feature.

Evidence: [IDbConnectionFactory.cs](Storage/IDbConnectionFactory.cs).
The [administration backup](../WafIsh.Administration/Modules/Dashboard/Controllers/DashboardSettingsController.cs) currently executes SQLite SQL.

Define a provider-owned backup operation with consistency, encryption, cancellation, and temporary-file ownership rules.
Extend FEAT-01 capability metadata to describe backup support.

Acceptance: Administration requests a backup without parsing a provider connection string.
Unsupported providers return an explicit capability result.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../tests/WafIsh.Regression/evidence/review4567/README.md).
