# Wafish naming alignment

Completed: 2026-11-14. The implementation remains in the working tree.

Aligned public API names, project and assembly names, resources, reflection strings, scripts, and documentation.
Updated source references and public API contracts. Preserved captured historical evidence output.

The renamed solution builds with zero warnings and errors.
Both regression runners pass after the rename. Three isolated NuGet consumers pass.
Package verification now removes its previous generated packages before packing.
This prevents stale packages with equivalent identifiers from supplying old assemblies.

See [build](build.txt), [regression](regression.txt), [administration](administration.txt), and [packages](packages.txt).
Read the [consumer migration](../../../../docs/naming-migration.md) before upgrading.
