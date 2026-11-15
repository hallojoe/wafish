# Wafish 1.0.0-rc.1

Prepared: 2026-11-15. Status: release candidate; publication pending.
This is the first public release candidate. It is not a final availability announcement.

## Scope

R01–R35 are complete. R36–R55 remain deferred.
The initial support target is Linux x64 with .NET 10.
Windows and macOS are additional verification targets, not advertised support commitments for this candidate.
Use matching versions of all Wafish packages.

## Included capabilities

- Optional administration and React UI packages; runtime-only hosting remains available.
- Authenticated management, operation permissions, safe logging, and bounded request processing.
- Encrypted SQLite storage, UTC migrations, backups, logical quotas, and restoration checks.
- Plugin ordering, host capability requirements, bounded retirement, and controlled policy clocks.
- Atomic configuration changes, previews, idempotent receipts, and versioned change sets.
- Instance leases, registration readiness, ordered endpoint selection, and graceful draining.
- Shared public cache fills, explicit invalidation, circuit jitter, and maintenance windows.
- Bounded metrics, latency distributions, and OpenTelemetry instruments.
- Service-discovery refresh, including bounded Kubernetes EndpointSlice queries.

See [registration and operations](need-seven-features.md) and [feature contracts](need-six-features.md).
See [security boundaries](security.md) before configuring authentication, logging, or external plugins.

## Installation rehearsal

The candidate is staged as local NuGet packages under `artifacts/package-checks`.
`python3 scripts/verify-packages.py` builds three consumers outside repository build imports.
Each consumer uses a fresh package cache and source mapping to the local staging feed.
Checks cover runtime, administration, embedded UI assets, native SQLite loading, encryption, and incorrect-key rejection.
No external package feed receives uploads during this rehearsal.

After approved publication, install matching `1.0.0-rc.1` packages from the approved feed.
Start with [MinimalGateway](../samples/MinimalGateway/README.md) or [SampleGateway](../samples/SampleGateway/README.md).

## Version and compatibility policy

Reviewed API snapshots define the initial public contract until publication.
This candidate replaces the repository's unpublished `2.3.0.30` development version.
It does not claim compatibility with an earlier supported public release.
Prerelease changes require reviewed API and configuration migration notes.
After the first stable release, breaking public contracts require a new major version.
Compatible features use minor versions; compatible fixes use patch versions.
Never replace an already published package version.
After publication, select the supported published version for SDK package-baseline validation.

## Operational limits

- Administrator authentication does not provide tenant or route ownership isolation.
- Registration failover waits when the previous gateway cannot confirm ownership release.
- Destination maintenance windows are process-local and disappear on restart.
- EndpointSlice tests use controlled HTTP fixtures; live Kubernetes validation remains deployment-specific.
- Legacy offsetless database timestamps are interpreted as UTC. Invalid legacy text remains unchanged.
- External AI, notification, and discovery integrations require deployment-specific credentials and validation.
- Performance reports from one local run do not establish production capacity or regression budgets.

## Verification

See [candidate evidence](../tests/Wafish.Regression/evidence/release-candidate/README.md).
Hosted CI passed for preparation commit `bf493b2`, including all three package/storage platforms.
[Verify run 43717485726](https://github.com/hallojoe/wafish/actions/runs/43717485726) passed all five jobs.
Package hashes identify the exact local staged artifacts.
Publication and the final release tag require separate approval.
