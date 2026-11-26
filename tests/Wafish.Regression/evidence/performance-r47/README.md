# R47: selected plugin combinations verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 290 regression cases passed, including 12 benchmark cases. See [results](regression.json).
- All 23 functional benchmark scenarios passed. See [results](first.json).
- Four combined-feature rows passed without request failures. See [results](combined.json).
- Combined checks used HTTPS/2, paced events, scheduled arrivals, and selected plugin combinations.
- Changed executable benchmark lines reached 90.74% coverage: 333 of 367 lines. See [coverage](changed-coverage.json).
- Coverage combines linked shared sources across processes, relative to audit commit `e92d2d7`.

The matrix accepts at most eight explicit sets from six supported plugins.
Tests reject unknown, duplicate, and oversized selections.
Cache and compression execute together. WAF rejects an attack request within a four-plugin combination.
Manifests record selected plugins, effective settings, execution order, and configuration hashes.
Native YARP receives the same workload and request encoding.

Run from the repository root with .NET SDK 10 available:

```bash
dotnet build Wafish.slnx -c Release -m:2
export WAFISH_VERIFICATION_DIRECTORY="$(mktemp -d)"
dotnet dotnet-coverage collect -f xml -o "$WAFISH_VERIFICATION_DIRECTORY/coverage.xml" \
  bash tests/Wafish.Regression/evidence/performance-r47/validate.sh
python3 tests/Wafish.Regression/evidence/performance-r47/changed-coverage.py \
  "$WAFISH_VERIFICATION_DIRECTORY/coverage.xml"
```

The [validation script](validate.sh) checks functional results and reconciles scheduled arrival counts.
The [coverage script](changed-coverage.py) enforces the 80% threshold for changed executable benchmark lines.

Compatibility: existing benchmark suites remain available. No public package API changed.
Report schema version 5 records plugin selections and request encoding.
Cache comparisons use an explicitly public synthetic response and a warm cache.
These short, instrumented runs verify behavior. They do not establish production performance budgets.
