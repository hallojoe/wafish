# Package API checks

Run `python3 scripts/verify-packages.py` from the repository root after a Release solution build.
The script packs public libraries, checks APIs, and runs three isolated package consumers.
Run `python3 tests/Wafish.PackageChecks/check-negative.py` to verify rejection of a removed public method.

The tool reads public and protected declarations from compiled package assemblies through Roslyn.
It rejects missing assemblies and removed or changed baseline declarations. Additive APIs remain permitted.
SDK package validation also checks package consistency during packing.

See [baseline policy](../contracts/public-api/README.md). This check supplements isolated consumer compilation.
It does not prove semantic compatibility or every binary compatibility rule.

## Release versions

Release workflows derive the version from the GitHub release tag, with an optional leading `v`.
Manual publishing runs use their version input. Local builds default to `0.0.0-dev`.

Use the same version for the Release build and package checks:

```bash
dotnet build Wafish.slnx -c Release -p:Version=1.1.0-rc.1 -p:PackageVersion=1.1.0-rc.1
python3 scripts/verify-packages.py --version 1.1.0-rc.1
```

Checks reject mismatched package metadata, internal dependency versions, and compiled informational versions.
This prevents `--no-build` packing from disguising stale assemblies with a new package version.
`check-negative.py` includes a stale-assembly check. API snapshots remain independent of release versions.
