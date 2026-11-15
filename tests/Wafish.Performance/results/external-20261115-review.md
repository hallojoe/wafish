# External benchmark evidence review

Reviewed: 2026-11-15. Retain these files as exploratory evidence:

- [JSON environment and results](external-20261115-003336-909-a83e2018.json)
- [CSV measurements](external-20261115-003336-909-a83e2018.csv)
- [Generated summary](external-20261115-003336-909-a83e2018.md)

The source revision is `963bb204fb7024de30315220e0ec210414662fcd`.
One 30-second run used four concurrent HTTP/1.1 callers after two seconds of warmup.
It recorded 1,363,695 requests, zero failures, and approximately 45,456 requests per second.
The recorded p99 upper-bound estimate is approximately 0.441 milliseconds.

The report omits the target configuration hash and host manifest.
External mode provides no process CPU or memory measurements; zero means unavailable.
No matched native-YARP run or repeat distribution was recorded.
Therefore this result is not a release performance gate, comparison baseline, or production capacity claim.
The original measurement files remain unchanged.
