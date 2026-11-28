# Initial CI performance baseline

Baseline run: [46111207302](https://github.com/hallojoe/wafish/actions/runs/46111207302).
Measured revision: `f7385ac58179a733cb0a76eaf81131d153ff18ec`.
All profiles returned `baseline_ready`. All measured requests completed without errors, rejection, or incomplete responses.
This record qualifies a regression reference. It does not establish production capacity.

## Conditions

The workflow used `ubuntu-24.04`, with Ubuntu 24.04.5 LTS and .NET 10.0.15.
The reported CPU was Intel Xeon 6975P-C, with four logical processors.
The process and operating system architectures were X64. Server GC was disabled in the runner.
The checkout was clean.

Each scenario used five repetitions, twenty-second warmups, and ten-second measurement periods.
Requests used HTTP/1.1, concurrency 16, and precomputed backend payloads.
Native YARP and Wafish used matched workloads and mandatory functional preflight.
Streaming used eight 1,024-byte chunks, separated by 50 milliseconds.

| Profile | Gateway/scenario pairs | Maximum throughput spread | Maximum P95 spread | Maximum P99 spread |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 9 | 5.46% | 8.04% | 16.92% |
| Plugins | 4 | 3.04% | 13.67% | 12.88% |
| Streaming | 2 | 0.30% | 0.00% | 2.01% |
| Policy limit | — | 20% | 30% | 40% |

Spread means `(maximum - minimum) / median` across the five repetitions.
The unchanged regression limits are 10% throughput loss, 20% P95 increase, and 25% P99 increase.
These remain engineering guardrails, not calibrated service objectives.

## Warmup correction

The earlier [three-second warmup run](https://github.com/hallojoe/wafish/actions/runs/46109827403) failed qualification.
Baseline and plugin profiles were noisy. Streaming qualified. No request failures occurred.
First repetitions had lower throughput and higher tail latency than later repetitions.
Native plain-text throughput spread reached 21.2%; its P95 spread reached 57.9%.
The WAF and rate-limit combination reached 52.3% P95 spread and 73.8% P99 spread.

This pattern suggested incomplete warmup. It did not prove a specific runtime optimization caused the difference.
The workflow warmup increased from three to twenty seconds. Measurement duration, repetitions, workloads, and thresholds stayed unchanged.
The job limit increased from 45 to 60 minutes to accommodate longer warmups.
All three profiles then qualified. No samples were removed and no noise limits were relaxed.

## Evidence identity

The qualification policy SHA-256 is:

```text
e90ff601a28e4b703c7b43d6f03d7e59beb3f90e07c388d1b8eb7bad01c4fa99
```

Raw JSON report hashes:

| Profile | SHA-256 |
| --- | --- |
| Baseline | `eb300840978df409de94ba17444a5793e966cf934b7d446223d90ba44e400474` |
| Plugins | `f78e3b6b963b162792a2e045b510b31501bc6e38dd6a64735b7642d75b09eb4d` |
| Streaming | `f077a41dc5ddca2e7ccb24c83ef450e907cf336f9670fe7069fd64dfa1625e63` |

The run retains JSON, CSV, and Markdown in artifact `performance-reports`, ID `15249888962`.
Its archive digest is `sha256:f0f3f9f59f1f3ecdb05e131c1fbb7965a193201f0a3cc6e05a862c4f31223c0f`.
Artifact `performance-comparison`, ID `15250406614`, retains the decisions and policy.
Its archive digest is `sha256:c40ef30f9d7531664efae3bff492aa60f6a5bd451b0eb534c84ade687dd90693`.
These artifacts expire on 2027-02-26. Preserve them before expiry for longer retention.
A run identifier alone cannot recover expired artifacts.

## Independent repeat comparison

Run [46112707318](https://github.com/hallojoe/wafish/actions/runs/46112707318) selected the qualified baseline explicitly.
It measured the same clean source revision in a compatible reported environment.
The workflow verified baseline provenance and downloaded the selected reports before measurement.
No request failures occurred.

| Profile | Decision | Maximum throughput spread | Maximum P95 spread | Maximum P99 spread |
| --- | --- | ---: | ---: | ---: |
| Baseline | `noisy` | 3.93% | 9.09% | 50.68% |
| Plugins | `passed` | 1.80% | 9.00% | 18.47% |
| Streaming | `passed` | 0.20% | 0.00% | 2.01% |

Of 45 metric comparisons, 44 passed. Wafish core's plain-text P99 exceeded the 40% spread limit.
Its five P99 measurements were 0.912, 0.684, 0.595, 0.885, and 0.566 milliseconds.
The median improved from 0.763 to 0.684 milliseconds. The spread still invalidated the performance decision.
The gate correctly failed the run. This is noise rejection, not a confirmed performance regression.
The failed candidate was not promoted to the baseline.

The repeated measurements support retaining the current conservative thresholds.
They do not establish that every hosted run will be stable.
No tolerance was relaxed, no repetition was excluded, and no failed comparison was relabeled as passed.
Future noisy runs require investigation and fresh measurements before performance approval.
The selected baseline remains the earlier run whose three profiles qualified.

Repeat report hashes:

| Profile | SHA-256 |
| --- | --- |
| Baseline | `89f19aa01ce5f632da6c4311059549e7fe8f156b6ba1edc1c239a00d436859db` |
| Plugins | `e7ceb47eea8ef4b35d6db2c9e743efe55f87ba0a43693412171f442e07df9071` |
| Streaming | `699a6e454ad60a31eb97f8b863ef494a0d6f10ce1f02d38a90f89ae4694f3c3e` |

Repeat artifact `performance-reports`, ID `15250199788`, retains raw JSON, CSV, and Markdown.
Its archive digest is `sha256:84f89254ac2ab04048b093dd124a77b19ecb1f7f33abb64a0565a0d30b48574b`.
Artifact `performance-comparison`, ID `15250658917`, retains both inputs, decisions, provenance, and policy.
Its archive digest is `sha256:4a3d86f1760d098d9f579af01e605c98702d96115a4d6fac1665e19007ff1d79`.
These artifacts also expire on 2027-02-26.

## Use and limits

Select this run explicitly with `baseline-run-id=46111207302`.
Follow the [operator guide](performance-budgets.md) for candidate comparisons.
Candidate environment, workload, configuration hashes, and result matrices must match.
Source revisions may differ. Runtime or hosted runner changes can make a later run incompatible.
Do not weaken compatibility checks or noise limits to force a passing comparison.
Requalify deliberately when measurement conditions change.
