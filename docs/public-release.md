# Public package release scope

Use matching package versions and the .NET 10 SDK and runtime.
Check [release reconciliation](../PLAN.md#r56) before publication or claims about feed availability.
Historical candidate scope and platform results are in [the archive](../ARCHIVE.md#archive-docs-public-release-md).
Reviewed API snapshots define the first-release contract until publication.

## Consumer entry points

Start with [MinimalGateway](../samples/MinimalGateway/README.md) for runtime and SQLite.
Use [SampleGateway](../samples/SampleGateway/README.md) for authenticated administration and the React console.
Use matching Wafish package versions. The optional administration layer trusts authenticated administrators.
Hosts can restrict operation permissions through shared settings, authenticated claims, or custom authorization.
See [operation permissions](../src/Wafish.Administration/README.md#operation-permissions).

## Upgrade behavior

- Route log redaction augments mandatory credential defaults. Malformed JSON capture is now masked.
- Storage quotas are opt-in logical byte budgets. See [quota allowances](../src/Wafish.Plugin.ProxyLog/README.md#storage-quotas).
- Plugin ordering and host requirements are additive manifest fields. Invalid replacements preserve the active domain.
- External middleware callbacks now execute in validated order. Check external plugins before upgrading.
- Capability discovery is optional. Legacy gRPC servers return an empty version-zero result through the client helper.
- UI build metadata identifies embedded assets and administration contract version 1.
- Startup registration waits now use jitter. Cluster retry budgets default to 100 extra attempts per second.
- Metrics expose cumulative route and cluster instruments. Their scopes must not be summed as independent gateway traffic.
- Statistic descriptors are additive. Unknown extension values remain readable.
- Benchmark transport faults remain opt-in and loopback-only.

## Version and publication policy

Public API snapshots are stored under `tests/contracts/public-api`.
Run `python3 scripts/verify-packages.py` against each candidate before publication.
Review baseline updates separately. Additive API changes must preserve previous declarations and interface requirements.
Document intentional source, binary, configuration, or behavioral breaks before changing their baselines.
Assign a new package version before publication. Never replace an already published package version.
