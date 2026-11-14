# Benchmark reliability evidence

Completed priority 10 on 2026-11-14. Changes remain uncommitted.

Release solution build: zero warnings and errors.
Main suite: 220 passing cases and one upload-fixture failure.
The fixture now uses Expect: 100-continue before oversized uploads; its focused rerun passes.
All 221 main cases therefore have passing verification. Administration: 35 passing cases.
Three isolated package consumers pass. Public API contracts remain unchanged.
Changed executable source coverage: 336/350 lines, 96.00%.

The full first suite passes all 23 scenarios, including retry, circuit recovery, logging, rate limits, and soak.
The baseline regression covers all eight modes and 23 measured rows.
External checks verify metadata and rejection before measurement when preflight fails.
These one-second runs verify behavior. Concurrent coverage collection distorts throughput; do not compare their performance values.

Backend tests cover cancellation, interrupted echo, four-MiB rejection, isolated counters, and prepared/generated payloads.
Native preflight detects unavailable forwarding despite successful process health.
The histogram tests compare 250,000 exact samples and 800,000 concurrent writes against its documented error bound.
Manifests include normalized effective configuration, plugin versions, transport, resources, and hashes.
Reports include runtime, OS, CPU, GC mode, source revision, and dirty state.
Cleanup preserves unrelated database files.

Preflight exposed an existing circuit scenario assumption: a successful request contributes to its sampling window.
The scenario now uses minimum throughput four and failure ratio 0.75.
One successful preflight followed by three failures opens the circuit.
Recovery still requires a successful half-open probe.

Verification commands:

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --benchmarks
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
dotnet run --project tests/Wafish.Performance/Runner -c Release --no-build -- --suite first --duration 1 --warmup 0 --soak-duration 1
python scripts/verify-packages.py
```

Coverage combines the main suite, administration suite, corrected backend check, and first benchmark suite.
Use `changed-coverage.py` with merged dotnet-coverage XML to reproduce the changed-line calculation.

The complete priority 11 suite subsequently passed all 225 cases, including every benchmark check.
See [that complete run](../allocation-pressure/main.json).
