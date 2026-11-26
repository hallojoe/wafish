# R41: compression policy verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 296 core regression cases passed. See [results](regression.json).
- The focused policy case passed, including the raw-snapshot null fallback. See [results](focused.json).
- Changed executable source coverage: 100% (7/7). See [coverage](changed-coverage.json).
- All 19 package compatibility checks passed against unchanged reviewed snapshots. See [results](public-api.log).

Checks cover restricted algorithms, empty lists, wildcards, duplicate qualities, malformed qualities, and identity fallback.
Each codec level matches output from the corresponding .NET codec.
Decoded bytes and Vary headers retain their existing behavior.
The null fallback test was added after the full run. Its focused coverage supplements the full-run report.

Compatibility: additive configuration properties. Omitted settings retain both codecs and the shared compression level.
No reviewed API snapshots changed.
