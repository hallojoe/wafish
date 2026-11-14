# ARCH-04 through ARCH-09 verification

Text paths and labels were updated for Wafish and English. Historical results below remain from their original runs.

Owner: Codex. Date: 2026-11-14.
Base commit: `6ba871a`. Tested working tree includes uncommitted changes. No commit was created.

- Debug solution build: zero warnings and errors.
- Release solution build: zero warnings and errors before the final gRPC transaction change.
- Main regression executable: 50 checks passed after the gRPC transaction change.
- Administration-only executable: 12 checks passed.
- Published browser checks: login, cluster creation, route creation, validation rejection, and storage rejection passed.
- AI settings rendered. The unregistered MCP checkbox remained disabled.
- Browser storage failure used a temporary SQLite abort trigger. The trigger was removed after verification.
- All test hosts and browser sessions were stopped.

Commands:

```sh
dotnet build Wafish.slnx --nologo
dotnet build Wafish.slnx -c Release --nologo
dotnet run --project tests/Wafish.Regression --no-build
dotnet run --project tests/Wafish.Administration.Regression --no-build
dotnet publish samples/SampleGateway -c Release --no-build -o /tmp/wafish-arch07-publish
```

## Coverage

ARCH-04: proxy-only pipeline, cache hits, response transforms, health endpoints, and deployment endpoint roles.
ARCH-05: independent AI/MCP registration, disabled services, authenticated AI APIs, and disabled MCP controls.
ARCH-06: transaction rollback, isolated readers, restart recovery, stable identities, batch operations, and atomic gRPC registration.
ARCH-07: validation, version conflicts, storage failure, activation failure, HTTP status codes, and browser error handling.
ARCH-08: plugin replacement failure, in-flight request draining, persisted state, and legacy state import.
ARCH-09: administration-only hosting, published sample hosting, package migration instructions, and new storage/result contracts.

## Limitations

Docker execution remains unavailable because socket access was denied.
The existing Monaco worker localization 404 remains tracked under UI-01.
The sidebar settings link was obscured at the default browser viewport. Direct navigation worked. Track this under UI-01.
Security changes are verified separately. These records do not mark unfinished security or UI tasks complete.
