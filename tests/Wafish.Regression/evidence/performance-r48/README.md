# R48: multiple-destination verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 297 core regression cases passed, including the benchmark group. See [results](regression.json).
- Changed executable source coverage: 86.21% (50/58). See [coverage](changed-coverage.json).
- Two-destination tests verified RoundRobin over HTTP/1.1 and FirstAlphabetical over HTTP/2.
- Native, same-host native, and Wafish full modes used identical upstream lists and policies.

The [three-destination report](three-destinations.json) retains manifests, destination settings, and completed-response distribution.
All six result rows had zero failed requests and zero unknown destination identities.
RoundRobin counts differed by at most one response across the three destinations.
This short run verifies routing and accounting. It is not a performance baseline or throughput claim.
It ran alongside regression checks.

Invalid lists reject duplicate ports, mismatched transports, non-loopback addresses, excessive destinations, and conflicting options.
Default single-destination operation remains covered by the existing benchmark cases.

Compatibility: no package APIs changed. The native host still has no Wafish project references.
Report schema remains 5, with additive destination metadata and per-result counters.
Counters describe returned response identities. Caches and retries can make them differ from backend execution counts.
