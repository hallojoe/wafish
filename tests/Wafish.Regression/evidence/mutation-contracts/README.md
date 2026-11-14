# Mutation, registration, protocol, and plugin contracts

Completed: 2026-11-14. Owner: Codex. Changes remain uncommitted.

Completed Administration IMP-01, Client FEAT-02, Grpc IMP-01/02, and Plugin.Abstractions IMP-01/02.

## Behavior

Configuration and plugin HTTP writes share status, outcome, and nullable revision fields.
Failure responses use Problem Details. Existing endpoint identifiers and data remain available.
Plugin persistence faults return `StorageFailed` while retaining the previous enabled state.
The AI plugin tool now uses `IGatewayPluginManager` instead of an obsolete reflected assembly name.

Registration result APIs preserve validation, credentials, conflicts, storage, and activation outcomes.
Transport failures retain uncertainty about remote commits. Caller cancellation propagates through mutation wrappers.
RPC deadlines use the configured registration timeout. Unexpected server errors expose sanitized transport failures.
Existing route catch-all parameters remain intact.

Fixed protobuf fixtures cover old requests, old responses, additive fields, unknown fields, and optional presence.
Package checks now reject added abstract requirements on existing interfaces and abstract classes.
Negative checks also verify removed members, changed record constructors, and missing assemblies.

Lifecycle checks cover repeat calls, failed partial startup, cancellation, shutdown races, and peer cleanup after faults.
The coordinator blocks reconciliation before shutdown and attempts uncancelled cleanup after partial startup.
Health query failures expose safe summaries.

A full run exposed background audit writes surviving host disposal.
Audit disposal now joins the initial read and all accepted writes before releasing storage ownership.
A controlled test holds both phases and verifies repeated disposal and rejected late writes.
Collectible-context tests yield between collection cycles so completed continuations can leave their stacks.

## Verification

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --contracts
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
python scripts/verify-packages.py --update-api
python tests/Wafish.PackageChecks/check-negative.py
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
UI_TEST_NO_BUILD=true npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

Release build: zero warnings and errors. Main suite: 200 passing cases. Administration: 35 passing cases.
Focused contracts: 13 groups. Frontend: 32 cases. Browser: 23 cases. API compatibility guards: six cases.
Changed C# production-line coverage: 235/269, or 87.36%.
Both changed frontend expressions are covered. Overall frontend coverage remains low; priority 8 expands workspace coverage.

See [build](build.txt), [main](regression.txt), [administration](administration.txt), [coverage](coverage.txt),
[frontend coverage](frontend-coverage.txt), [browser](browser.txt), and [compatibility guards](api-negative.txt).

## Compatibility and limits

[HTTP and client migration](../../../../docs/mutation-contracts.md) documents the numeric `status` and named `outcome` fields.
The React client accepts the new format and legacy application status fields.
Unknown revisions stay `null` and are hidden in the UI.
Boolean registration wrappers remain available. Cancellation now propagates instead of becoming `false`.

[Lifecycle requirements](../../../../docs/plugin-lifecycle-contract.md) define resource cleanup ownership.
Audit storage remains best effort. Disposal joins accepted attempts; failed writes are logged without a durability guarantee.
An unresponsive audit repository can delay disposal. No timeout abandons accepted writes during normal disposal.
API checks cover declared source and binary surfaces, not every external plugin's behavior.

Public API baselines were updated for the documented contracts. All three isolated package consumers pass.
See [package verification](packages.txt).
