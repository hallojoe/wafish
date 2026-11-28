# Performance budgets

The [comparison tool](../scripts/check-performance-budgets.py) checks earlier managed benchmark reports against new measurements.
The [budget policy](../tests/Wafish.Performance/budgets.json) defines minimum run lengths, regression tolerances, and noise limits.
It supports schema-5 `baseline` and `plugins` reports, including paced streaming and scheduled arrivals.
External endpoints and the functional `first` suite are excluded.

## Select a baseline in CI

1. Run the **Performance budgets** workflow on the intended source revision without a baseline run ID.
2. Review all three qualification decisions and their raw reports. All profiles must report `baseline_ready`.
3. Record the successful workflow run ID as the selected baseline.
4. Run the workflow on a candidate revision with that ID in `baseline-run-id`.

Each run retains raw JSON, CSV, and Markdown in `performance-reports` for 90 days.
Comparison decisions, the selected baseline, and the policy are retained in `performance-comparison`.
Decisions record source revisions and SHA-256 hashes of both reports and the policy.
Baseline selection is explicit. A failed or noisy comparison never replaces the baseline.
CI accepts baseline artifacts only from successful runs of this workflow in the same repository.
Download selected reports before artifact retention expires if longer history is required.

The workflow measures these profiles sequentially:

| Profile | Coverage |
| --- | --- |
| `baseline` | Native YARP, Wafish core, and Wafish full across the standard payload matrix |
| `plugins` | Native YARP and explicit `none`, `waf+rate-limit`, and `response-cache+compression` sets |
| `streaming` | Native YARP and Wafish full with eight paced 1024-byte chunks, 50 ms apart |

Each profile uses HTTP/1.1, precomputed payloads, concurrency 16, five repetitions, ten-second measurements, and twenty-second warmups.
Twenty-second warmups reduce first-repetition startup effects observed during initial qualification.
The policy thresholds and ten-second measurement duration remain unchanged.
Functional preflight remains mandatory before measurement.
These are representative regression workloads, not production capacity measurements.

## Selected initial baseline

Use run [`46111207302`](https://github.com/hallojoe/wafish/actions/runs/46111207302) as the initial baseline.
All three profiles qualified at revision `f7385ac58179a733cb0a76eaf81131d153ff18ec`.
The [qualification record](performance-baseline-2026-11.md) identifies its environment, report hashes, and measured variation.
A compatible repeat passed plugins and streaming but failed one P99 noise limit. That candidate was not promoted.
This selection applies only to compatible reports. It does not approve unrelated runner hardware or workload changes.

```bash
gh workflow run performance-budgets.yml --ref YOUR_CANDIDATE_BRANCH \
  -f baseline-run-id=46111207302
```

## Comparison rules

Reports must have identical workload settings, transport, encoding, arrival model, histogram configuration, and result matrices.
Host configurations and hashes must match, including backend payload allocation and plugin composition.
Environment metadata must match, including runtime, operating system, CPU model, processor count, architectures, and GC mode.
Both source checkouts must be clean. Source revisions and capture timestamps may differ.
Listener addresses are part of configuration identity. Use the same isolated addresses for both runs.

GitHub-hosted machines can differ. Such reports return `incompatible`, even when both use `ubuntu-24.04`.
Do not weaken identity checks to compare different machines. Use a controlled runner environment when stable comparisons are required.

Every scenario requires all declared repetitions. Missing or duplicate repetitions make the report invalid.
Request errors, policy rejections, incomplete responses, dropped arrivals, and cancelled arrivals fail qualification.
Failure counters are checked in both reports before performance comparisons.

The initial policy applies these engineering guardrails:

| Metric | Allowed median regression | Maximum repetition spread |
| --- | ---: | ---: |
| Requests per second | 10% decrease | 20% |
| P95 latency | 20% increase | 30% |
| P99 latency | 25% increase | 40% |

Spread is `(maximum - minimum) / median`, expressed as a percentage.
An excessive spread returns `noisy` for either report.
A median breach confirms a regression only when the complete observed ranges exceed the allowed tolerance.
An overlapping range returns `noisy`. These checks do not claim statistical confidence intervals.
All nonzero outcomes fail the CI gate. A noisy result requires investigation and new measurements.

The initial tolerances are configurable guardrails, not calibrated production service objectives.
Review changes to the policy separately from candidate performance changes.

## Local commands

Build the complete Release solution before running managed hosts.
Select unused loopback addresses and keep them identical across baseline and candidate measurements.
Do not run competing benchmarks simultaneously.

```bash
python3 scripts/check-performance-budgets.py \
  --candidate artifacts/candidate/performance-run.json \
  --output artifacts/qualification.json

python3 scripts/check-performance-budgets.py \
  --baseline artifacts/baseline/performance-run.json \
  --candidate artifacts/candidate/performance-run.json \
  --output artifacts/comparison.json
```

A report argument can also name a directory containing exactly one `performance-*.json` file.
Inputs are bounded to 16 MiB. Invalid JSON, non-finite measurements, and incomplete metadata fail validation.
Output paths cannot replace input files.

| Exit code | Result | Meaning |
| ---: | --- | --- |
| 0 | `passed` | Comparable measurements meet the budgets |
| 0 | `baseline_ready` | A standalone report passes qualification; no regression comparison occurred |
| 1 | `regression` | At least one metric has a confirmed budget breach |
| 2 | `invalid` | Input, policy, metadata, or repetition validation failed |
| 3 | `incompatible` | Environment, workload, configuration, or result identities differ |
| 4 | `request_failure` | Either report contains failed, incomplete, rejected, or dropped work |
| 5 | `noisy` | Measurement variation prevents a reliable decision |

Each metric keeps its own status. A confirmed regression takes precedence over noise elsewhere.
Request failures take precedence over configuration incompatibility, and both remain visible in the decision.

Run `python3 scripts/test-performance-budgets.py` for deterministic comparator checks.
Run the executable regression suite with `--benchmarks` for the existing load harness checks.
