# R45 gate verification — 2026-11-28

These are two independent runs of unchanged source at `30000be`.
Each used five ten-second measurements after three-second warmups, concurrency four, and matching loopback addresses.
The workload compares Native YARP with the WAF/rate-limit plugin combination.
Both runs completed without request failures.

The reports have matching environment and configuration identities.
The comparator rejected both qualification and comparison as `noisy` (exit 5).
These files verify the gate's handling of unstable measurements. They are not approved CI performance baselines.
Do not interpret their timing differences as a code regression or a production capacity result.

- [Qualification decision](qualification.json)
- [Comparison decision](comparison.json)
- [Baseline reports](baseline/)
- [Candidate reports](candidate/)

Decisions retain the original artifact paths and SHA-256 hashes. The copied raw files are unchanged.
Use the [budget guide](../../../../docs/performance-budgets.md) for baseline qualification and comparison rules.
