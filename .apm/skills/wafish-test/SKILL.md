---
name: wafish-test
description: Select and run Wafish executable regression, administration, React, browser, contract, and coverage checks. Use for changed-behavior verification; not generic test-framework migration.
---

# Verify Wafish changes

Paths are relative to the repository root. Read scoped `AGENTS.md` files and the relevant test README.

1. Build the required Release projects first. Build the complete solution before suites that launch child hosts.
2. Read the current regression registry and runner help rather than assuming groups from another branch exist.
   `tests/Wafish.Regression/Program.cs` defines core groups; administration has its own executable suite.
3. Select the smallest relevant checks, then run the broader checks required by the affected project's instructions.

| Area | Entry point |
|---|---|
| Core regression | `dotnet run --project tests/Wafish.Regression -c Release --no-build` |
| Administration | `dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build` |
| Focused core case | Append `-- --case "exact registered name"` |
| Focused core group | Append `-- --group group-name` |
| React | Run `build`, `lint`, and `test:coverage` through npm in `src/Wafish.ReactUi/ClientApp` |
| Browser | Run the same frontend's `test:e2e` script; it owns hosts, ports, storage, and cleanup |
| Packages | Follow `tests/Wafish.PackageChecks/README.md` and `wafish-packages` |
| Website | Use `website/package.json` scripts, including build-time link checks |

These regression projects use `dotnet run`, not `dotnet test`.
Use one selection mechanism per invocation. Combining selection flags can match no cases.
Set `WAFISH_TEST_REPORT` to an absolute artifact path when retaining structured results.
Never infer success from an empty selection, partial output, or a process that has not exited.

## Ownership and evidence

Use the browser parent runner rather than starting its fixtures manually.
Use `UI_TEST_NO_BUILD=true` only after the required hosts and frontend have been built.
Browser-host fixture phases can take several minutes. Check progress and process state before diagnosing a hang.
Allow cleanup to finish. Do not delete temporary databases or stop hosts owned by another test run.
Live Redis checks must use an isolated instance; follow the test README's `WAFISH_REDIS_SERVER` instructions.

For changed executable code, meet scoped coverage requirements; project guidance normally requires at least 80%.
Restore `dotnet-coverage` through the local tool manifest when needed.
Keep coverage modules aligned with the exact source revision and build configuration.
Do not combine stale reports after line movement. Count partially covered ranges conservatively and explain the calculation.
Reviewed API, protobuf, and route baselines are contracts. Do not regenerate them to hide a failing compatibility check.

## Validation

Confirm exit codes and report statuses. State passed, failed, and unrun checks precisely.
When a fixed test reruns separately, retain that distinction from the original full-suite result.
Historical narratives belong in `ARCHIVE.md`; retained raw reports remain evidence for their recorded revision only.
