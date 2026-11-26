---
name: wafish-plugin-development
description: Add or change Wafish plugins, bindings, execution plans, or owned runtime resources while preserving lifecycle and compatibility contracts. Not for ordinary host configuration edits.
---

# Develop Wafish plugins

1. Read the nearest `AGENTS.md`, the existing sibling plugin, and `docs/plugin-lifecycle-contract.md`.
   Inspect `src/Wafish.Plugin.Abstractions` before adding contracts or lifecycle requirements.
   Use `docs/guide/plugins.md` for supported configuration and administration behavior.
2. Keep package boundaries intact. The core does not depend on Runtime, Administration, or ReactUi.
   Keep plugin identifiers stable across manifests, schemas, bindings, and execution plans.
   Preserve existing plugin implementations; avoid adding required abstract interface members without an approved compatibility change.
3. Define configuration bounds, defaults, scope, and validation before request-time behavior.
   Validate candidates before persistence and activation. Keep stored and active state unchanged after precommit rejection.
   Preserve stable route and cluster identities during renames and batch operations.
4. Reuse immutable execution plans for request processing. Do not deserialize binding configuration on every request.
   Keep request work bounded and cancellation-aware. Restore wrapped response bodies and dispose owned resources exactly once.
   Retired resources must wait for owned request leases before disposal; shutdown must not restart resources.
5. Add operation permissions and route-contract entries when management endpoints change.
   Use shared authorization and error outcomes. Do not make a plugin controller public by omission.
   Recheck persisted versus active revisions when adding mutation behavior.
6. Select contract, cancellation, streaming, replacement, and shutdown checks using `wafish-test`.
   Update the component README with actual behavior and limits. Keep historical completion records in `ARCHIVE.md`.

## Validation

Verify defaults, invalid configuration, cancellation, resource replacement, and failure behavior relevant to the change.
Run required core, administration, and compatibility checks. Preserve reviewed snapshots unless the requested change explicitly updates the contract.
