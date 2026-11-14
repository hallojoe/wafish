# Repository branding cleanup

Verified: 2026-11-14.

The initial case-insensitive search found 938 references across 84 tracked files, including hidden CI configuration.
The cleanup uses `Wafish` for display text and `wafish` for paths, prefixes, and metadata keys.
The test environment variable follows the existing uppercase convention: `WAFISH_REDIS_SERVER`.
Package project and repository links match the configured Git remote: `https://github.com/hallojoe/wafish`.

The sample API prefix is `/wafish`. The React workspace remains `/console`.
Registration metadata and Redis prefixes use the same lowercase naming.
See [migration effects](../../../../docs/naming-migration.md#repository-branding-and-configured-names).

## Verification

| Check | Result |
|---|---|
| Release solution build | Zero warnings and errors |
| Main regression runner | 230 checks pass |
| Administration runner | 35 checks pass |
| Live Redis runner | All seven scenario groups pass with the renamed environment variable |
| Frontend lint | Pass |
| Frontend unit tests with coverage | 81 tests pass |
| Browser tests | 30 tests pass, including authentication, API calls, realtime, and custom prefixes |
| Isolated package consumers | Three consumers pass; public API checks pass |
| Package metadata inspection | All 19 manifests use Wafish metadata and the current repository URL |
| Aspire topology | Resource and AppHost restarts preserve registrations, credentials, route identity, and browser links |
| Changed executable C# coverage | 3/3 lines covered; declarations and comments are excluded |
| Current documentation links | 94 local links resolve |
| Final case-insensitive search | No old-brand matches in tracked contents or paths |

Commands:

```bash
dotnet build Wafish.slnx -c Release
dotnet tool run dotnet-coverage collect -f cobertura -o /tmp/wafish-branding-coverage.xml 'dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll'
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
WAFISH_REDIS_SERVER=/path/to/redis-server dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --redis
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
python3 scripts/verify-packages.py
python3 scripts/check-topology.py
```

## Archived reports

Forty-eight archived files contain naming replacements only.
A comparison against the previous commit confirms that measurements and test outcomes are unchanged.
These reports remain historical evidence; their labels now follow the current project name.

The verification topology used temporary state and was stopped after testing.
No user AppHost was running when this task started.
