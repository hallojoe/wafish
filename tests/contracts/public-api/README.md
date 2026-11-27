# First-release API baseline

Recorded: 2026-11-14. Source package version: 2.3.0.30.
User decision: treat these as first-release packages. Use reviewed API snapshots until publication.
These snapshots define the initial supported surface. No earlier released-package baseline is required.

`python3 scripts/verify-packages.py` rejects missing assemblies and removed or changed declarations.
Use `--update-api` only for a reviewed baseline change. Review the resulting JSON diff.
After publication, configure `PackageValidationBaselineVersion` against the supported released package version.
Retain these source snapshots for review and keep migration notes for intentional breaks.

Items 14–16 preserve existing public constructors and add clock-aware overloads.
Stored legacy offsetless timestamps now mean UTC. Metric retention resets removed identities and limits series admission.
See [migration notes](../../../docs/security.md) and [verification](../../../ARCHIVE.md#archive-tests-wafish-regression-evidence-review141516-readme-md).

The redirect addition records the new plugin package, redirect preview DTOs/controller, and execution stage `Redirect = 250`.
It adds no required interface members and removes no existing declarations.
The manifest requires host capability `wafish.redirect-endpoint` version 1.

The claims-transform addition records its new package API. Existing declarations remain unchanged.
It requires `wafish.claims-transform` version 1 and compiles outbound mappings before activation.
