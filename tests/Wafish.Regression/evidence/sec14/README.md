# Native database replacement

Task: SEC-14. Owner: Codex. Date: 2026-11-14.
Tested source: uncommitted working tree based on `6ba871a`.

Replaced `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 with `SQLite3MC.PCLRaw.bundle` 2.7.0.
Runtime queries report SQLite3 Multiple Ciphers 2.7.0 and SQLite 3.54.1.
The published sample contains the replacement library and no retired SQLCipher library.
NuGet repository-signature verification passed for the native package.

The connection factory preserves SQLCipher 4 compatibility when a password protects a file database.
Existing connection strings remain usable. Explicit cipher URI parameters and pooling settings remain unchanged.
Plaintext storage remains plaintext. Sample and Aspire defaults do not enable encryption.

## Results

| Check | Result | Evidence |
|---|---|---|
| Release solution build | Zero warnings and errors | [Build](build-release.log) |
| Main regression suite | 68 passing checks | [Regression](regression.log) |
| Focused native storage suite | Five passing checks | [Storage](storage.log) |
| Administration regression suite | 20 passing checks | [Administration](administration.log) |
| Browser workflows | 17 passing checks | [Browser](browser.log) |
| NuGet vulnerability audit | No reported advisories | [Audit](dependency-audit.log) |
| Native package signature | Valid NuGet repository signature | [Signature](package-signature.log) |
| Published container | Encrypted storage, backup, React, and realtime pass | [Container](container.log) |
| Original-engine interoperability | Both engines retain each other's writes | [Round trip](legacy-roundtrip.log) |

Compatibility fixtures were generated with the original SQLCipher native library.
See [fixture provenance](../../fixtures/storage/README.md).
Tests cover plain SQLite and SQLCipher 4 files, schema migration, write/reopen, unusual filenames, and integrity checks.
Encrypted files reject absent and incorrect passwords.
The loaded native engine must meet the reviewed minimum versions, rather than relying only on managed package metadata.

The administration backup check holds an encrypted database open with committed WAL changes.
Its downloaded backup contains those changes, passes integrity checks, and rejects reads without the password.
Backup failures return HTTP 503 instead of copying an incomplete live database file.

The published-container check explicitly enables encryption with a public test-only password.
The original SQLCipher 4.5.2 library reads that backup and writes a marker.
The replacement library reads the marker and writes another marker.
The original library reads both markers and reports a clean integrity check.
The extracted published native library reports SQLite 3.54.1 and SQLite3MC 2.7.0.
The test container was stopped normally and removed.

## Commands

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release -- --storage
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
dotnet list Wafish.slnx package --vulnerable --include-transitive
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
podman build -f samples/SampleGateway/Dockerfile -t localhost/wafish:verification .
```

The native package signature was checked with `dotnet nuget verify <native-package.nupkg> --all`.
Tests ran on Linux x64 and in the Debian-based .NET 10 container.
The workflow now includes focused storage checks on Linux, Windows, and macOS.
Windows and macOS execution awaits CI; it was not performed locally.
No production database was modified, rekeyed, or converted during verification.

The documented native dependency blocker is resolved.
External AI and notification delivery still require deployment-specific credentials and checks.
No production deployment is included.

Sources: [maintainer releases](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/main/CHANGELOG.md),
[SQLCipher compatibility](https://github.com/utelle/SQLite3MultipleCiphers-NuGet#how-to-open-an-existing-database-encrypted-with-sqlcipher),
and [SQLite security fixes](https://sqlite.org/cves.html).
