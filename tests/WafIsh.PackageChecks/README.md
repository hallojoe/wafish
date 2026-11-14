# Package API checks

Run `python3 scripts/verify-packages.py` from the repository root after a Release solution build.
The script packs public libraries, checks APIs, and runs three isolated package consumers.
Run `python3 tests/WafIsh.PackageChecks/check-negative.py` to verify rejection of a removed public method.

The tool reads public and protected declarations from compiled package assemblies through Roslyn.
It rejects missing assemblies and removed or changed baseline declarations. Additive APIs remain permitted.
SDK package validation also checks package consistency during packing.

See [baseline policy](../contracts/public-api/README.md). This check supplements isolated consumer compilation.
It does not prove semantic compatibility or every binary compatibility rule.
