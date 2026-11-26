# R40: compression measurements verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 295 core regression cases passed, including allocation pressure and sample telemetry. See [results](regression.json).
- Changed executable source coverage: 80.36% (45/56). See [coverage](changed-coverage.json).
- Focused byte-accounting and cardinality checks passed. See [results](focused.json).
- All 19 package API compatibility checks passed against unchanged reviewed snapshots. See [results](public-api.log).

Checks measure Gzip, Brotli, identity bypass, small-body expansion, prefix buffering, synchronous writes, and producer failures.
Output counters equal the actual encoded body length, including codec framing.
Decoded responses remain byte-for-byte equal to their inputs.
Aborted responses retain separate outcomes. Disabled bindings emit no measurements.
Route churn stays within 128 named labels plus one overflow label.
The wrapper restores the response body and leaves the downstream stream open.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build
```

Compatibility: no public package API changed. Existing negotiation and streaming bounds remain in effect.
The sample metrics pipeline now subscribes to `Wafish.Plugin.Compression`.
Metrics describe accepted body writes, not confirmed client delivery or transport overhead.
Use completed responses for savings calculations; aborted writes can provide only partial counts.
