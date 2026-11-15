# Need-6 follow-up: R29–R35

Completed on 2026-11-15. Base commit: `0be18f0`.

- R29: focused administration regression modules preserve scenario names, order, shared fixtures, and cleanup.
- R30: bounded public cache-fill sharing with independent waiter cancellation.
- R31: bounded circuit recovery jitter sampled once per opening.
- R32: atomic route, cluster, and binding change sets with both revision checks and retry receipts.
- R33: ordered registration endpoints with stable identity and confirmed ownership release.
- R34: authenticated destination maintenance windows, expiry, and bounded recovery probes.
- R35: bounded, paginated Kubernetes EndpointSlice discovery with explicit credential access.

Implementation commits: `dbe65ae`, `bd11f81`, `a1cdf05`, `1857068`, `3e0c9c9`, `b8b0408`, and `e9b66ab`.
Follow-up commits: `630f7b7` and `189538e`.

## Verification

- Release solution build: zero warnings and errors.
- Full main suite: 283 passed.
- Final feature selection: 21 passed, including three additional regression cases.
- Unique main cases verified: 286.
- Administration suite: 44 passed. Focused API checks also pass with expanded failure assertions.
- Browser suite: 30 passed.
- Three isolated package consumers passed: runtime, administration, and React UI.
- Public API compatibility passed. All previous declarations remain; 228 declarations were added.
- Changed production executable-line coverage: 493/553, or 89.15%.

See [case results](checks.json), [coverage](changed-line-coverage.json), and [feature contracts](../../../../docs/configuration-policy-contracts.md).
The original 42 administration scenario names retain their relative execution order.

Full main and administration runs collected coverage after the final production changes.
Focused feature, client, and administration runs supplement that coverage.
Only tests changed between those runs. The final EndpointSlice case also verifies a readiness transition during refresh.
Public API baselines were updated after compatibility checks against the previous baselines passed.

```bash
dotnet build Wafish.slnx -c Release
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --group need-six
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --group client
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll --need-six
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
python3 scripts/verify-packages.py
```

Collect coverage with `dotnet tool run dotnet-coverage collect -f xml -o REPORT.xml 'COMMAND'`.
Run `changed-coverage.py` with the collected XML paths to reproduce the changed-line result.
The calculation compares production source against the recorded base commit.

## Acceptance evidence

Cache tests hold one fill while concurrent waiters cancel or time out.
Private responses, upstream failures, purges, and generation changes cannot supply invalid shared entries.
Admission tests enforce the owner and waiter limits without leaking slots.

Controlled circuit clocks verify jitter bounds, stable deadlines, and half-open limits.
Maintenance tests use real HTTP forwarding to verify destination selection, retry filtering, expiry, and in-flight completion.
Administration checks enforce maintenance permissions and retain actor, reason, expiry, and probe settings.

Combined change-set checks inject binding-write and receipt-write failures.
They verify complete rollback, explicit conflicts, separate stored/active revisions, cancellation, and single correlated success audits.
Additional checks preserve plugin access to core configuration during dependency resolution.
HTTP checks cover missing credentials, both required mutation permissions, validation, conflicts, and storage failures.

Failover checks cover unavailable endpoints, stable owner selection, missing lease acknowledgements, lost replies, and cancellation.
Concurrent retries cannot create owners on both gateways.
The actual endpoint factory rejects untrusted TLS certificates and disposes its transports.

EndpointSlice fixtures cover pagination, continuation encoding, IPv4/IPv6, port selection, condition defaults, and duplicate addresses.
They also cover readiness changes, endpoint removal, malformed responses, response bounds, cancellation, and refresh deadlines.
Provider failures preserve the last valid set. Valid empty lists remove retired endpoints.

## Operational limits

Failover waits if the previous gateway cannot confirm ownership release.
Maintenance windows are process-local and are cleared on restart.
EndpointSlice verification uses controlled HTTP fixtures; no live Kubernetes cluster was tested.
Packages were verified on Linux. No package was published. No commit was pushed.

R29–R35 are complete. R36–R55 remain deferred: 20 optional tasks.
