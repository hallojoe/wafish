# Administration lifecycle and HTTPS fixtures

Completed: 2026-11-14. Owner: Codex. Implementation remains in the working tree.

Completed ReactUi.Host IMP-01 and IMP-03.
Completed Administration.Regression IMP-02, FEAT-01, and FEAT-02.

The controller inventory produces 1,044 credential probes across 87 actions and four modes.
Anonymous and invalid requests are rejected. Authorized probes stop before action execution and model binding.
Login and logout are the explicit public exceptions. Transport policy still applies.
The matrix exposed unprotected circuit-breaker actions. They now share the administration authorization scope.

Session checks cover distinct login tokens, capacity, generation races, sign-out, two-factor changes, expiry, and disposal.
Real HTTP and WebSocket checks verify concurrent access rejection and existing connection termination.
Basic authentication and API-key checks verify credential rotation.
Cleanup checks inject failed assertions and startup failures before checking resource disposal and subsequent host startup.

Browser fixtures cover HTTP and HTTPS, nested prefixes, assets, APIs, and realtime access across three authentication modes.
The parent runner removes private fixture directories after success, failure, failed child launch, and handled interruption.

## Verification

```bash
dotnet build Wafish.slnx -c Release --no-restore
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
npm --prefix src/Wafish.ReactUi/ClientApp run test:fixtures
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
UI_TEST_NO_BUILD=true npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

The solution build has zero warnings and errors.
Results: 168 main cases, 32 administration cases, five parent cleanup checks, and 23 browser cases pass.
Frontend lint and the existing unit suite pass. No frontend production source changed in this milestone.
Changed production C# coverage is 87/89 lines, or 97.75%.
Naming-only changes are excluded from this measurement; solution and package checks cover those changes separately.
See [coverage](coverage.txt), [build](build.txt), [administration](administration.txt), and [browser results](browser.txt).
The coverage script reads merged XML from both regression runners and the recorded changed-line list.

## Compatibility and limits

JWT tokens must originate from this host's login endpoint. Signature validation alone no longer grants administration access.
Sessions are host-local, expire after eight hours, and end on restart.
Use session affinity when multiple hosts serve the same dashboard.
A full 10,000-session registry returns 503 without evicting active sessions.
Successful two-factor changes require all JWT users to sign in again.
Custom authorization delegates retain ownership of their authentication policy.
Requests authorized before revocation can finish. Existing credential-based hubs revalidate every second.

Tests use ephemeral, self-signed certificates and loopback listeners. They do not test external certificate infrastructure.
The parent runner handles normal termination signals. An uncatchable parent termination cannot run cleanup.
