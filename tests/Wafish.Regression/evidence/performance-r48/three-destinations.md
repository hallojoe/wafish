# Wafish Performance Results

- Generated: 2026-11-26 01:51:29
- OS: Unix 7.2.8.9
- Logical processors: 8
- Transport: HTTP/1.1 exact cleartext; upstream: matched.
- Runtime: 10.0.14
- Concurrency: 1
- Arrivals: closed-loop; target: 0/s; queue: 128.
- Service latency starts at dispatch. Scheduled latency starts at the intended arrival. Queue delay includes scheduler lag.
- Latency: fixed-memory histogram; upper-bound percentiles, ≤1% relative error above 1 μs.
- Full environment and configuration manifests: adjacent JSON report.
- Warmup: 0s; duration: 1s; repetitions: 1; result: median RPS run

| Gateway | Scenario | RPS | P50 ms | P95 ms | P99 ms | Errors % | CPU % | Memory MB |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Native YARP | json-small | 1,959 | 0.43 | 0.95 | 2.49 | 0.000 | 17.4 | 112.0 |
| Wafish full | json-small | 2,381 | 0.37 | 0.76 | 1.14 | 0.000 | 27.2 | 148.8 |
| Native YARP | plain | 1,357 | 0.62 | 1.25 | 2.92 | 0.000 | 12.1 | 98.6 |
| Wafish full | plain | 1,701 | 0.53 | 0.89 | 1.62 | 0.000 | 17.6 | 141.5 |
| Native YARP | post-1kb | 2,273 | 0.35 | 0.73 | 2.44 | 0.000 | 20.8 | 115.1 |
| Wafish full | post-1kb | 2,152 | 0.40 | 0.78 | 1.31 | 0.000 | 20.7 | 144.1 |

| Gateway | Scenario | Offered | Started | Dropped | Cancelled before start | Responses | Queue P95 ms | Scheduled P95 ms |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Native YARP | json-small | 1960 | 1960 | 0 | 0 | 1960 | 0.00 | 0.95 |
| Wafish full | json-small | 2381 | 2381 | 0 | 0 | 2381 | 0.00 | 0.76 |
| Native YARP | plain | 1360 | 1360 | 0 | 0 | 1360 | 0.00 | 1.25 |
| Wafish full | plain | 1702 | 1702 | 0 | 0 | 1702 | 0.00 | 0.89 |
| Native YARP | post-1kb | 2276 | 2276 | 0 | 0 | 2276 | 0.00 | 0.73 |
| Wafish full | post-1kb | 2152 | 2152 | 0 | 0 | 2152 | 0.00 | 0.78 |

| Gateway | Scenario | Response bytes | Body reads | First-byte samples | First byte P95 ms | Completion P95 ms | Maximum read gap P95 ms |
|---|---|---:|---:|---:|---:|---:|---:|
| Native YARP | json-small | 346920 | 1960 | 1960 | 0.94 | 0.95 | 0.00 |
| Wafish full | json-small | 421437 | 2381 | 2381 | 0.76 | 0.76 | 0.00 |
| Native YARP | plain | 2720 | 1360 | 1360 | 1.23 | 1.25 | 0.00 |
| Wafish full | plain | 3404 | 1702 | 1702 | 0.89 | 0.89 | 0.00 |
| Native YARP | post-1kb | 2330624 | 2276 | 2276 | 0.72 | 0.73 | 0.00 |
| Wafish full | post-1kb | 2203648 | 2152 | 2152 | 0.77 | 0.78 | 0.00 |

## Relative throughput versus Native YARP

- Wafish full / json-small: 21.5% RPS, -54.4% P99 latency.
- Wafish full / plain: 25.3% RPS, -44.4% P99 latency.
- Wafish full / post-1kb: -5.3% RPS, -46.6% P99 latency.

> Same-machine benchmark. The report records the arrival model. Use the relative differences to assess middleware overhead; do not treat these numbers as production capacity planning without cross-machine load generation.
