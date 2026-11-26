# R46: passkey verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 297 core regression cases passed. See [results](regression.json).
- All 47 administration cases passed. See [results](administration.json).
- All 87 UI unit tests passed. See [results](frontend-tests.log).
- All 31 browser cases passed, including a real WebAuthn ceremony through a virtual authenticator. See [results](browser.log).
- Frontend lint passed without warnings.
- Changed C# coverage: 93.72% (179/191). See [coverage](changed-coverage.json).
- Changed instrumented frontend lines: 100% (53/53). See [coverage](frontend-coverage.json).
- All 19 package API compatibility checks passed against unchanged reviewed snapshots. See [results](public-api.log).
- NuGet reported no vulnerable administration dependencies, including transitive dependencies. See [results](dependencies.json).

Checks cover enrollment, user verification, signatures, origin rejection, replay, challenge expiry, session binding, credential limits,
challenge capacity, startup validation, private file permissions, persistence, revocation, and password recovery with configured TOTP.
The browser confirms the secure session cookie and avoids credential storage in localStorage.
The authorization matrix includes every new action across all four existing authentication modes.
Only the two passkey login actions are public; network restrictions remain active.
The route inventory adds seven passkey endpoints and preserves the existing entries.

The full core and browser runs preceded the final credential-material storage-bound check.
The final Release build and complete administration suite passed after that check.
The [route contract check](contracts.json) also passed after the final build.
C# coverage uses the final administration report plus core/UI modules from the core run.
The older administration module was excluded before combining reports, preventing shifted-line attribution.
Frontend coverage excludes type-only changes and JSX lines without separate LCOV sequence points.
An additional account-page test verifies the configured passkey panel.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release --no-build
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

Compatibility: additive options, APIs, and UI controls. Passkeys remain disabled by default.
FIDO2 4.3.3 is a new administration dependency. Password and TOTP recovery behavior remains available.
The credential file belongs to one configured JWT account and one gateway process.
