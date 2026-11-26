# R45: performance budgets verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 291 regression cases passed, including 13 benchmark cases. See [results](regression.json).
- The final arithmetic adjustment passed the [focused checks](focused.json) after rebuilding.
- Changed executable benchmark lines reached 84.68% coverage: 94 of 111. See [coverage](changed-coverage.json).
- The [coverage script](changed-coverage.py) compares changes against `adb955c` and enforces an 80% minimum.
- Workflow YAML and embedded Bash commands passed local syntax checks.

Tests exercise stable passes, throughput and latency regressions, overlapping ranges, excessive spread, and request failures.
They also verify incompatible settings, missing repetitions, invalid policies, dropped arrivals, and the comparison CLI.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build
```

Compatibility: existing load modes remain unchanged. No public package API changed.
Comparison accepts schema 5 managed reports. External and functional reports do not establish compatible performance baselines.
The manual workflow requires an explicit baseline commit and fails on regressions, request failures, noise, or invalid inputs.
Hosted CI execution remains unverified locally. No remote workflow was dispatched.

Two uninstrumented runs compared Native YARP and Wafish full across three payloads, with five repetitions each.
Settings: two-second measurements, one-second warmup, concurrency eight, identical loopback ports.
See [baseline](baseline.json), [candidate](candidate.json), and [comparison](comparison.json).
All 60 measurement rows had zero request failures.
The comparison returned exit code 2: 18 metric checks passed, 11 reported noise, and one exceeded its budget.
Wafish full POST throughput fell from 22,111 to 12,105 requests/second across unchanged-source runs.
This is a measured budget failure, not evidence that a code change caused it.
The result demonstrates why local short runs must not become an automatic production baseline.
No tolerances were relaxed. Select a reviewed baseline on a controlled worker before using the manual gate.
