# Execution evidence

Baseline commit: `6ba871a0f0304957d3f178931434f5e9fc6cf16b`.
Date: 2026-11-13. Owner: Codex.

## Baseline

- SDK: 10.0.403. Original targets: net8.0 and net9.0.
- Original build: `dotnet build Wafish.slnx --nologo`; zero warnings and errors.
- Original regression runtime: isolated ASP.NET Core 8.0.34.
- Original regression: 22 passes and three failures. See [output](baseline-net8.txt).
- Dependency audit: `dotnet list Wafish.slnx package --vulnerable --include-transitive --format json`.
- [Audit result](baseline-audit.json): SQLitePCLRaw.lib.e_sqlite3 2.1.10 was flagged across five projects.

## Framework migration

All projects now target net10.0, per the user's instruction.
Managed SQLite packages moved from Microsoft.Data.Sqlite 9.0.4 to Microsoft.Data.Sqlite.Core 10.0.15.
The Core package avoids a competing native bundle and preserves SQLCipher encryption.
This follows Microsoft's [encryption package guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption).
The initial .NET 10 regression reproduced the same three failures. See [output](baseline-net10.txt).
The native SQLite dependency still requires SEC-10 verification.

## Security reproduction

Run: `dotnet run --project tests/Wafish.Regression`.
The added security checks reproduced ten failures before the urgent fixes.
See [pre-fix output](security-before.txt).
The pre-fix output also includes the three historical runtime failures.

## Final .NET 10 verification

Tested working tree: baseline commit above, plus the uncommitted implementation changes.

| Check | Command or procedure | Result |
|---|---|---|
| Debug build | `dotnet build Wafish.slnx --nologo` | Zero warnings and errors; [output](build-net10-debug.txt) |
| Release build | `dotnet build Wafish.slnx -c Release --nologo` | Zero warnings and errors; [output](build-net10-release.txt) |
| Regression executable | `dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll` | All 42 checks pass; [output](regression-net10.txt) |
| Publication | `dotnet publish samples/SampleGateway/SampleGateway.csproj -c Release --no-build -o /tmp/aneiang-net10-verified-publish --nologo` | Succeeded |
| Published sample | Start the published DLL with temporary encrypted storage, generated credentials, and loopback HTTP/gRPC endpoints | Nine checks pass; [output](smoke-net10.txt) |
| Dependency audit | `dotnet list Wafish.slnx package --vulnerable --include-transitive --format json` | No known package vulnerabilities reported; [output](audit-net10.json) |
| Docker | `docker info` | Docker socket access denied; image build and container execution remain unverified |

The published sample ran in Production with MCP disabled and automatic browser launch disabled.
Checks covered login, authenticated pages/API, anonymous rejection, static CSS, health, readiness, and encrypted database creation.
No application errors appeared during startup, requests, or shutdown.

RuntimeHostChecks verifies forwarding without dashboard registration and checks duplicate configuration startup registration.
SqliteEncryptionChecks uses production storage registration and migrations.
It writes data, recreates the service provider, reopens the database, and rejects missing or incorrect keys.
The loaded native library reports SQLCipher 4.5.2 community and SQLite 3.39.2.
SEC-10 must review that native version separately. A clean package audit does not prove native-library security.

All three original regression failures now pass. Logging services were missing from two test fixtures.
The legacy plugin-state import failure required a production implementation fix.

The remaining architecture, security, Aspire, and UI tasks remain tracked in PLAN.md.
